def run(values: list[str]) -> list[str]:
    trimmed = (value.strip() for value in values)
    return list(dict.fromkeys(value for value in trimmed if value))
