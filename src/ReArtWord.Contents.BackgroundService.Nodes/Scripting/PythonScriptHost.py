"""Static contract inspection and single-call execution; inspection never evals user code."""
import ast
import asyncio
import contextlib
import inspect
import json
import sys


def annotation(node):
    if isinstance(node, ast.Name):
        kind = {"int": "integer", "float": "number", "str": "string", "bool": "boolean", "Any": "any"}.get(node.id)
        return {"type": kind} if kind else None
    if isinstance(node, ast.Constant) and node.value is None:
        return {"type": "any", "optional": True}
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        try:
            return annotation(ast.parse(node.value, mode="eval").body)
        except SyntaxError:
            return None
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.BitOr):
        left, right = annotation(node.left), annotation(node.right)
        if left and right:
            if left == {"type": "any", "optional": True}:
                return dict(right, optional=True)
            if right == {"type": "any", "optional": True}:
                return dict(left, optional=True)
    if isinstance(node, ast.Subscript):
        name = node.value.id if isinstance(node.value, ast.Name) else node.value.attr if isinstance(node.value, ast.Attribute) else ""
        if name in ("list", "List"):
            element = annotation(node.slice)
            return {"type": "array", "elementType": element} if element else None
        if name == "Optional":
            value = annotation(node.slice)
            return dict(value, optional=True) if value else None
        if name in ("dict", "Dict") and isinstance(node.slice, ast.Tuple) and len(node.slice.elts) == 2:
            key, value = (annotation(item) for item in node.slice.elts)
            return {"type": "object", "keyType": key, "valueType": value} if key and key.get("type") in ("string", "integer", "boolean") and value else None
    return None


def infer(node, arguments):
    if isinstance(node, ast.Constant):
        if node.value is None:
            return {"type": "any", "optional": True}
        kind = {bool: "boolean", int: "integer", float: "number", str: "string"}.get(type(node.value))
        return {"type": kind} if kind else None
    if isinstance(node, ast.Name):
        return arguments.get(node.id)
    if isinstance(node, (ast.List, ast.Tuple)):
        elements = [infer(item, arguments) for item in node.elts]
        if not elements:
            return {"type": "array", "elementType": {"type": "any"}}
        if all(item == elements[0] and item is not None for item in elements):
            return {"type": "array", "elementType": elements[0]}
    if isinstance(node, ast.Dict) and all(isinstance(key, ast.Constant) and isinstance(key.value, str) for key in node.keys):
        fields = {key.value: infer(value, arguments) for key, value in zip(node.keys, node.values)}
        if all(fields.values()):
            return {"type": "object", "properties": fields}
    if isinstance(node, ast.Compare):
        return {"type": "boolean"}
    if isinstance(node, ast.BinOp):
        left, right = infer(node.left, arguments), infer(node.right, arguments)
        if left and right and left == right and left.get("type") in ("integer", "number", "string"):
            if isinstance(node.op, ast.Div):
                return {"type": "number"}
            return left
    if isinstance(node, ast.UnaryOp):
        return infer(node.operand, arguments)
    return None


def returns(statements):
    for statement in statements:
        if isinstance(statement, ast.Return):
            yield statement.value or ast.Constant(value=None)
        elif not isinstance(statement, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            for field in ("body", "orelse", "finalbody"):
                yield from returns(getattr(statement, field, []))
            for handler in getattr(statement, "handlers", []):
                yield from returns(handler.body)


def analyze(request):
    module = ast.parse(request["code"], filename="script.py")
    if request.get("syntaxOnly"):
        return {"diagnostics": [], "contract": None}
    entries = [node for node in module.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == "run"]
    if len(entries) != 1:
        return {"diagnostics": ["Define exactly one run function."], "contract": None}
    entry = entries[0]
    if entry.args.kwarg or entry.args.posonlyargs or entry.decorator_list:
        return {"diagnostics": ["run must use named arguments without decorators, positional-only arguments or **kwargs."], "contract": None}
    manual = request.get("manualContract") or {}
    arguments, diagnostics = {}, []
    defaults = dict(zip([parameter.arg for parameter in entry.args.args][-len(entry.args.defaults):], entry.args.defaults)) if entry.args.defaults else {}
    defaults.update({parameter.arg: value for parameter, value in zip(entry.args.kwonlyargs, entry.args.kw_defaults) if value is not None})
    for parameter in entry.args.args + entry.args.kwonlyargs:
        kind = annotation(parameter.annotation)
        if kind is None and parameter.arg in defaults:
            kind = infer(defaults[parameter.arg], {})
        if kind is None:
            kind = manual.get("arguments", {}).get(parameter.arg)
        arguments[parameter.arg] = kind
    variadic = entry.args.vararg
    if variadic:
        element = annotation(variadic.annotation)
        chosen = manual.get("arguments", {}).get(variadic.arg) or {"type": "any"}
        if element is None:
            element = chosen.get("elementType", {"type": "any"}) if chosen.get("type") == "array" else chosen
        arguments[variadic.arg] = {"type": "array", "elementType": element, "optional": True}
    result = annotation(entry.returns)
    if result is None:
        values = [infer(node, arguments) for node in returns(entry.body)]
        if values and all(value is not None and value == values[0] for value in values):
            result = values[0]
    if result is None:
        result = manual.get("result")
    if result is None:
        result = {"type": "any"}
    suggestion = {"arguments": {name: kind or {"type": "any"} for name, kind in arguments.items()}, "result": result or {"type": "any"}}
    for name in defaults:
        suggestion["arguments"][name] = dict(suggestion["arguments"][name], optional=True)
    if variadic:
        suggestion["variadicArgument"] = variadic.arg
    return {"diagnostics": diagnostics, "contract": suggestion if not diagnostics else None, "suggestedContract": suggestion}


def dictionary_value(value, kind, outbound=False):
    """Preserve typed dictionary keys at the JSON boundary without guessing text keys."""
    if value is None:
        return None
    kind = kind or {}
    if kind.get("type") == "array" and isinstance(value, list):
        return [dictionary_value(item, kind.get("elementType"), outbound) for item in value]
    if not isinstance(value, dict):
        return value
    key_kind = kind.get("keyType", {}).get("type")
    result = {}
    for key, item in value.items():
        if outbound:
            if key_kind is None:
                converted = key
            elif key_kind in ("integer", "int64") and type(key) is int:
                converted = str(key)
            elif key_kind == "boolean" and type(key) is bool:
                converted = "true" if key else "false"
            elif key_kind == "string" and type(key) is str:
                converted = key
            else:
                raise ValueError("script.dictionaryKeyType")
        elif key_kind in ("integer", "int64"):
            converted = int(key)
            if str(converted) != key:
                raise ValueError("script.dictionaryKeyFormat")
        elif key_kind == "boolean":
            if key not in ("true", "false"):
                raise ValueError("script.dictionaryKeyFormat")
            converted = key == "true"
        else:
            converted = key
        if converted in result:
            raise ValueError("script.dictionaryKeyDuplicate")
        child = kind.get("valueType") or kind.get("properties", {}).get(str(key))
        result[converted] = dictionary_value(item, child, outbound)
    return result


def execute(request):
    namespace = {"__name__": "contents_script"}
    with contextlib.redirect_stdout(sys.stderr):
        exec(compile(request["code"], "script.py", "exec"), namespace)
        function = namespace["run"]
        signature = inspect.signature(function)
        contract = request.get("contract") or {}
        arguments = {name: dictionary_value(value, contract.get("arguments", {}).get(name)) for name, value in request["arguments"].items()}
        variadic = None
        rest = ()
        for parameter in signature.parameters.values():
            if parameter.default is not inspect.Parameter.empty and arguments.get(parameter.name) is None:
                arguments.pop(parameter.name, None)
            if parameter.kind == inspect.Parameter.VAR_POSITIONAL:
                variadic = parameter.name
                rest = tuple(arguments.pop(variadic, []))
        bound = signature.bind_partial(**arguments)
        bound.apply_defaults()
        if variadic:
            bound.arguments[variadic] = rest
        result = function(*bound.args, **bound.kwargs)
        if inspect.isawaitable(result):
            result = asyncio.run(result)
    return {"completed": True, "value": dictionary_value(result, contract.get("result"), True)}


def exception_diagnostic(error, depth=0):
    """Send bounded failure details to the parent's protected log without source or locals."""
    try:
        message = str(error)
        truncated = len(message) > 2048
        if truncated:
            message = "[omitted: length limit]"
        frames = []
        trace = error.__traceback__
        while trace is not None and len(frames) < 32:
            code = trace.tb_frame.f_code
            frames.append(f'  at {code.co_name} ({code.co_filename}:{trace.tb_lineno})')
            trace = trace.tb_next
        stack = "\n".join(frames)
        if len(stack) > 6144:
            stack = "[omitted: length limit]"
            truncated = True
        truncated = truncated or trace is not None
        cause = error.__cause__ or (None if error.__suppress_context__ else error.__context__)
        children = []
        if cause is not None:
            if depth < 2:
                children.append(exception_diagnostic(cause, depth + 1))
            else:
                truncated = True
        return {"type": type(error).__name__[:256], "message": message, "stackTrace": stack,
                "hResult": 0, "innerExceptions": children, "truncated": truncated}
    except BaseException:
        return {"type": "UnknownError", "message": None, "stackTrace": None,
                "hResult": 0, "innerExceptions": [], "truncated": True}


def main():
    with open(sys.argv[2], "r", encoding="utf-8") as source:
        request = json.load(source)
    failed = False
    try:
        response = analyze(request) if sys.argv[1] == "analyze" else execute(request)
        encoded = json.dumps(response, ensure_ascii=True, allow_nan=False, separators=(",", ":"))
    except (SyntaxError, RecursionError) as error:
        failed = sys.argv[1] != "analyze"
        response = {"diagnostics": ["Python syntax or nesting error at line " + str(getattr(error, "lineno", "unknown"))], "contract": None}
        encoded = json.dumps(response) if sys.argv[1] == "analyze" else json.dumps({"completed": False, "error": "script.executionFailed", "exception": exception_diagnostic(error)})
    except BaseException as error:
        failed = True
        encoded = json.dumps({"completed": False, "error": "script.executionFailed", "exception": exception_diagnostic(error)})
    if failed and len(encoded.encode("utf-8")) > max(1024, request.get("maximumDiagnosticBytes", 1048576)):
        encoded = json.dumps({"completed": False, "error": "script.executionFailed", "exception": {
            "type": "ScriptError", "message": "[diagnostic omitted: output limit]", "stackTrace": None,
            "hResult": 0, "innerExceptions": [], "truncated": True}})
    sys.stdout.write(encoded)


if __name__ == "__main__":
    main()
