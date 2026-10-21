#!/usr/bin/env python3
"""Run two isolated Core/ClientHost pairs through their public local HTTP APIs.
The ClientHosts own the existing UDP observers; this runner implements no UDP protocol.
"""
import argparse
import ipaddress
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid


class CheckFailure(RuntimeError):
    pass


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--core', required=True, type=Path, help='Published Core apphost or DLL; its whole directory is copied.')
    parser.add_argument('--client-host', required=True, type=Path, help='Published ClientHost apphost or DLL; its whole directory is copied.')
    for side in ('a', 'b'):
        parser.add_argument(f'--{side}-interface', required=True)
        parser.add_argument(f'--{side}-ip', required=True)
        parser.add_argument(f'--{side}-broadcast', required=True)
        parser.add_argument(f'--{side}-port', type=int, default=47740)
    parser.add_argument('--allow-same-nic-smoke', action='store_true', help='Explicit smoke mode; never reports multi-NIC verification.')
    parser.add_argument('--expect-isolated', action='store_true', help='Observe both distinct subnets for --timeout seconds; neither may discover the opposite Core.')
    parser.add_argument('--timeout', type=float, default=60, help='Seconds per read-only wait stage.')
    parser.add_argument('--output-root', type=Path, default=Path.cwd() / '.work' / 'manual-network-checks')
    return parser.parse_args()


def free_http_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8')
    args = arguments()
    args.output_root.mkdir(parents=True, exist_ok=True)
    root = Path(tempfile.mkdtemp(prefix='nic-check-', dir=args.output_root.resolve()))
    cluster = 'nic-check-' + uuid.uuid4().hex
    bindings = {side: dict(interfaceId=getattr(args, side + '_interface'), bindAddress=getattr(args, side + '_ip'),
                           broadcastAddress=getattr(args, side + '_broadcast'), port=getattr(args, side + '_port'))
                for side in ('a', 'b')}
    mode = 'same-nic-smoke' if args.allow_same_nic_smoke else 'same-pc-multi-nic'
    if args.expect_isolated:
        mode = 'same-pc-subnet-isolation'
    result = dict(clusterId=cluster, fixture=str(root), mode=mode,
                  physicalPcReachabilityVerified=False, networkChangesPerformed=False, bindings=bindings, steps=[], success=False)
    owned = []
    streams = []
    urls = {}
    current = dict(stage='preflight', target='runner')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def stage(name, target, evidence=None):
        current.clear()
        current.update(stage=name, target=target)
        event = dict(stage=name, target=target, time=time.time())
        if evidence is not None:
            event['evidence'] = evidence
        result['steps'].append(event)
        print(f'[{name}] {target}', flush=True)

    def require(condition, message):
        if not condition:
            raise CheckFailure(message)

    def alive():
        for label, process in owned:
            if process.poll() is not None:
                raise CheckFailure(f'{label} exited with code {process.returncode}; inspect its stdout/stderr files.')

    def wait(read_condition):
        end = time.monotonic() + args.timeout
        last = None
        while time.monotonic() < end:
            alive()
            try:
                value = read_condition()
                if value:
                    return value
            except (OSError, urllib.error.URLError, CheckFailure) as error:
                last = str(error)
            time.sleep(.2)
        raise CheckFailure('Expected condition was not reached on the specified binding. Last observation: ' + str(last))

    def http(side, method, path, data=None, target=None, request_id=None):
        current.update(client=side, targetPeer=target, endpoint=path)
        headers = {'Content-Type': 'application/json'}
        if target:
            headers.update({'X-Contents-Cluster': cluster, 'X-Contents-Peer': target})
        if request_id:
            headers['X-Contents-Request'] = request_id
        payload = None if data is None else json.dumps(data, ensure_ascii=False).encode('utf-8')
        request = urllib.request.Request(urls[side] + path, data=payload, headers=headers, method=method)
        try:
            response = opener.open(request, timeout=min(args.timeout, 20))
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read()
            body = json.loads(raw) if raw else None
            command = response.headers.get('X-Contents-Command')
            if not (200 <= response.status < 300):
                if request_id and command:
                    stage('query-original-command', target or side, dict(commandId=command, originalRequest=request_id, response=body))
                    def query():
                        status = http(side, 'GET', f'/client/commands/{command}/result')
                        reply = status.get('reply')
                        if reply is None and status.get('running'):
                            return None
                        require(reply is not None and reply.get('outcome') == 'succeeded', 'Original command is unconfirmed: ' + json.dumps(status))
                        return {'domain': reply.get('value', {}).get('json')}
                    return wait(query)['domain']
                raise CheckFailure(f'{method} {path}: HTTP {response.status}: {body}')
            return body

    def write(side, path, data, target):
        request_id = str(uuid.uuid4())
        stage('write-once', target, dict(client=side, path=path, requestId=request_id, domainOperation=data.get('operationId')))
        # A write is never automatically resent, including when no response headers arrive.
        return http(side, 'POST', path, data, target, request_id)

    def launch(label, executable, extra):
        output = open(root / (label + '.stdout.log'), 'wb')
        errors = open(root / (label + '.stderr.log'), 'wb')
        streams.extend((output, errors))
        command = [str(executable), *extra]
        if executable.suffix.lower() == '.dll':
            command.insert(0, 'dotnet')
        flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
        process = subprocess.Popen(command, cwd=executable.parent, stdout=output, stderr=errors, creationflags=flags)
        owned.append((label, process))
        stage('launched', label, dict(pid=process.pid, executable=str(executable)))
        return process

    def snapshot_members(side, target):
        state = http(side, 'GET', '/api/status', target=target)
        members = state['peers']
        require(len(members) == 2 and {p['peerId'] for p in members} == {'nic-a', 'nic-b'}, 'Core membership must contain exactly two Core peers, never the observers.')
        for peer in members:
            expected = bindings[peer['peerId'][-1]]
            require(peer['address'] == expected['bindAddress'] and peer['port'] == expected['port'], 'Observed Core address/port differs from the explicit binding: ' + json.dumps(peer))
        return state

    def ready_health(side, target, process, instance):
        end = time.monotonic() + args.timeout
        previous_status = None
        while True:
            alive()
            health = http(side, 'GET', '/health', target=target)
            if health.get('status') != previous_status:
                stage('core-health', f'{side}->{target}', dict(expectedPid=process.pid, expectedInstance=instance, response=health))
                previous_status = health.get('status')
            require(health['processId'] == process.pid and health['instanceId'] == instance, 'Health is not the fixed owned Core instance.')
            require(not health.get('startupProblem') and not health.get('synchronizationProblem'), 'Core reports a startup or synchronization problem: ' + json.dumps(health))
            if health['ready']:
                return health
            require(health['status'] == 'syncing', 'The discovered Core is neither ready nor synchronizing: ' + json.dumps(health))
            require(time.monotonic() < end, 'The fixed Core did not finish synchronizing within the deadline: ' + json.dumps(health))
            # Discovery is a past heartbeat; only inspect this same live instance until its startup sync finishes.
            time.sleep(.2)

    try:
        require(1 <= args.timeout <= 600, '--timeout must be between 1 and 600 seconds.')
        for side, binding in bindings.items():
            stage('preflight-binding', side, binding)
            require(bool(binding['interfaceId']), 'An explicit interface ID is required.')
            ip = ipaddress.ip_address(binding['bindAddress'])
            broadcast = ipaddress.ip_address(binding['broadcastAddress'])
            require(ip.version == 4 and broadcast.version == 4 and not ip.is_unspecified, 'Use explicit IPv4 addresses, not 0.0.0.0.')
            require(1 <= binding['port'] <= 65535, 'UDP port is outside 1..65535.')
        stage('preflight-topology', 'a<->b', bindings)
        if not args.allow_same_nic_smoke:
            require(bindings['a']['interfaceId'].casefold() != bindings['b']['interfaceId'].casefold(), 'Main mode requires distinct NIC IDs; same-NIC checks require --allow-same-nic-smoke.')
            require(bindings['a']['bindAddress'] != bindings['b']['bindAddress'], 'Main mode requires distinct assigned IP addresses.')
        require(bindings['a']['port'] == bindings['b']['port'], 'A Cluster uses a common UDP port; no port translation or fallback is performed.')
        if args.expect_isolated:
            require(not args.allow_same_nic_smoke, 'Subnet isolation requires distinct NICs.')
            require(bindings['a']['broadcastAddress'] != bindings['b']['broadcastAddress'], 'Subnet isolation requires distinct broadcast destinations.')
        checked_ips = set()
        for side, binding in bindings.items():
            ip, port = binding['bindAddress'], binding['port']
            if ip in checked_ips:
                continue
            stage('preflight-ip-assignment', side, binding)
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                # This unsent ephemeral socket only verifies that the IP is assigned.
                # The actual Core/Observer sockets always use the explicit UDP port.
                probe.bind((ip, 0))
            checked_ips.add(ip)
        copied = {}
        for label, source in (('core', args.core), ('client', args.client_host)):
            source = source.resolve(strict=True)
            require(source.is_file(), label + ' must be an executable file or DLL.')
            destination = root / (label + '-runtime')
            require(not destination.is_relative_to(source.parent), 'Output must not be nested inside a supplied runtime directory.')
            shutil.copytree(source.parent, destination)
            copied[label] = destination / source.name
        stage('isolated-copies-complete', 'runner', {key: str(value) for key, value in copied.items()})
        core_processes = {}
        for side in ('a', 'b'):
            stage('client-start', side)
            urls[side] = f'http://127.0.0.1:{free_http_port()}'
            launch('client-' + side, copied['client'], ['--listen', urls[side] + '/', '--state', str(root / ('client-' + side) / 'state')])
            inventory = wait(lambda: http(side, 'GET', '/client/network'))
            matching = [nic for nic in inventory['interfaces'] if nic['interfaceId'].casefold() == bindings[side]['interfaceId'].casefold()]
            require(len(matching) == 1 and matching[0]['available'], 'Specified NIC is missing or unavailable; no alternate NIC is selected.')
            addresses = [address for address in matching[0]['addresses'] if address['address'] == bindings[side]['bindAddress']]
            require(len(addresses) == 1, 'Specified IP is not assigned to the specified NIC.')
            require(addresses[0]['broadcastAddress'] == bindings[side]['broadcastAddress'], 'Specified broadcast does not match the assigned subnet.')
            network = http(side, 'POST', '/client/network', bindings[side])
            require(network['listening'] and network['selected'] == bindings[side], 'Observer did not bind the explicit configuration.')
            stage('observer-binding-verified', side, network)
        for side in ('a', 'b'):
            binding = bindings[side]
            local = root / ('core-' + side)
            local.mkdir()
            def literal(value):
                return json.dumps(str(value))
            configuration = '\n'.join([
                '[cluster]', 'id=' + literal(cluster), 'peerId=' + literal('nic-' + side), 'hostId=' + literal(socket.gethostname()),
                'interfaceId=' + literal(binding['interfaceId']), 'address=' + literal(binding['bindAddress']), 'broadcast=' + literal(binding['broadcastAddress']),
                'port=' + str(binding['port']), 'priority=' + ('1' if side == 'a' else '0'), 'initialize=' + ('true' if side == 'a' else 'false'),
                '[storage]', 'directory=' + literal(local / 'state'), '[files]', 'roots=[' + literal(local / 'content') + ']',
                '[logs]', 'directory=' + literal(local / 'logs'), 'interfaceId=' + literal(binding['interfaceId']), 'listenPort=0', ''])
            path = local / 'contents.toml'
            path.write_text(configuration, encoding='utf-8')
            core_processes[side] = launch('core-' + side, copied['core'], ['--config', str(path)])
        if args.expect_isolated:
            stage('observe-subnet-isolation', 'a<->b', dict(seconds=args.timeout, sharedClusterId=cluster))
            observed = set()
            end = time.monotonic() + args.timeout
            while time.monotonic() < end:
                alive()
                for side in ('a', 'b'):
                    clusters = http(side, 'GET', '/client/clusters')
                    result.setdefault('visibleClusters', {})[side] = clusters
                    ours = [item for item in clusters if item['clusterId'] == cluster]
                    require(len(ours) <= 1, 'The same cluster was discovered more than once.')
                    if ours:
                        peers = ours[0]['peers']
                        require(len(peers) == 1 and peers[0]['peerId'] == 'nic-' + side,
                                'A Core from the other subnet was discovered: ' + json.dumps(ours))
                        observed.add(side)
                time.sleep(.2)
            require(observed == {'a', 'b'}, 'Each observer must positively discover its own Core; silence does not prove isolation.')
            stage('subnet-isolation-confirmed', 'a<->b', result['visibleClusters'])
        else:
            for side in ('a', 'b'):
                stage('bilateral-discovery', side)
                def discovered():
                    clusters = http(side, 'GET', '/client/clusters')
                    result.setdefault('visibleClusters', {})[side] = clusters
                    ours = [item for item in clusters if item['clusterId'] == cluster]
                    result.setdefault('lastDiscovery', {})[side] = ours[0] if len(ours) == 1 else dict(found=False, visibleClusterCount=len(clusters))
                    if len(ours) != 1:
                        return None
                    peers = ours[0]['peers']
                    return ours[0] if len(peers) == 2 and {p['peerId'] for p in peers} == {'nic-a', 'nic-b'} and all(p['status'] == 'ready' for p in peers) else None
                found = wait(discovered)
                stage('discovered-own-cluster', side, found)
                for target_side in ('a', 'b'):
                    target = 'nic-' + target_side
                    stage('readonly-address-and-membership', f'{side}->{target}')
                    instance = next(peer['instanceId'] for peer in found['peers'] if peer['peerId'] == target)
                    ready_health(side, target, core_processes[target_side], instance)
                    stage('core-observed', f'{side}->{target}', snapshot_members(side, target))
            definition = dict(id='nic-pass', name='network verification', nodes=[dict(id='value', type='control.pass', settings={},
                inputs={'value': dict(kind='json', json=42, binary='')}, target=dict(scope='all', peers=['nic-a', 'nic-b']))], wires=[])
            revision = None
            for side in ('a', 'b'):
                origin = 'nic-' + side
                opposite = 'nic-' + ('b' if side == 'a' else 'a')
                definition['name'] = 'Saved through client ' + side
                stage('save-and-share', origin)
                save = write(side, '/api/workflows/save', dict(operationId=str(uuid.uuid4()), definition=definition, expectedRevision=revision), origin)
                require(all(save[phase]['outcome'] == 'succeeded' for phase in ('saved', 'shared', 'snapshot', 'activation')), 'Save/shared/snapshot/activation phases were not all confirmed: ' + json.dumps(save))
                revision = save['revision']
                remote = http(side, 'GET', '/api/workflows/nic-pass', target=opposite)
                require(remote['saved']['revision'] == revision, 'The opposite Core did not expose the saved revision.')
                stage('save-shared', origin, save)
                operation = str(uuid.uuid4())
                stage('all-targets-pass-42', origin)
                run = write(side, '/api/runs', dict(operationId=operation, workflowId='nic-pass', revision=revision), origin)
                if run.get('running'):
                    run = wait(lambda: (lambda item: item if not item.get('running') else None)(http(side, 'GET', '/api/runs/' + operation, target=origin)))
                require(run['outcome'] == 'succeeded' and run.get('report') is not None, 'Run did not complete with a full small result.')
                targets = run['report']['nodes'][0]['targets']
                require(len(targets) == 2 and {t['peerId'] for t in targets} == {'nic-a', 'nic-b'} and
                        all(t['result']['outcome'] == 'succeeded' and t['result']['outputs']['value']['json'] == 42 for t in targets), 'Both owned Core targets must return 42.')
                mirrored = http(side, 'GET', '/api/runs/' + operation, target=opposite)
                require(mirrored['outcome'] == 'succeeded', 'Confirmed execution was not shared to the opposite Core.')
                stage('bidirectional-run-confirmed', origin, run)
                snapshot_members(side, origin)
        result['success'] = True
    except (Exception, KeyboardInterrupt) as error:
        result['failure'] = dict(**current, exception=type(error).__name__, message=str(error),
                                errno=getattr(error, 'errno', None), winerror=getattr(error, 'winerror', None))
        print('FAILED ' + json.dumps(result['failure'], ensure_ascii=False), file=sys.stderr, flush=True)
    finally:
        stopped = []
        for label, process in reversed(owned):
            try:
                if process.poll() is None:
                    process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    # Only the Popen instance started by this invocation is killed.
                    process.kill()
                    process.wait(timeout=5)
                stopped.append(dict(target=label, pid=process.pid, stopped=True))
            except Exception as error:
                result['success'] = False
                stopped.append(dict(target=label, pid=process.pid, stopped=False, reason=str(error)))
        for stream in streams:
            stream.close()
        result['cleanup'] = stopped
        (root / 'result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(dict(success=result['success'], mode=result['mode'], result=str(root / 'result.json')), ensure_ascii=False), flush=True)
    return 0 if result['success'] else 1


if __name__ == '__main__':
    sys.exit(main())
