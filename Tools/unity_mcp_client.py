"""Small local HTTP MCP client for editor verification. Session state stays in memory."""
import json
import sys
import requests


class UnityMCP:
    def __init__(self, url='http://127.0.0.1:8080/mcp'):
        self.url = url
        self.headers = {'Accept': 'application/json, text/event-stream'}
        self.counter = 0
        self.rpc('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                 'clientInfo': {'name': 'grayling-repair', 'version': '1.0'}})
        requests.post(url, headers=self.headers,
                      json={'jsonrpc': '2.0', 'method': 'notifications/initialized'}, timeout=10)

    def rpc(self, method, params):
        self.counter += 1
        response = requests.post(self.url, headers=self.headers,
            json={'jsonrpc': '2.0', 'id': self.counter, 'method': method, 'params': params}, timeout=55)
        response.raise_for_status()
        if 'mcp-session-id' in response.headers:
            self.headers['mcp-session-id'] = response.headers['mcp-session-id']
        if 'text/event-stream' in response.headers.get('content-type', ''):
            messages = [json.loads(line[6:]) for line in response.text.splitlines() if line.startswith('data: ')]
            result = next(m for m in messages if m.get('id') == self.counter)
        else:
            result = response.json()
        if 'error' in result:
            raise RuntimeError(result['error'])
        return result['result']

    def call(self, name, **arguments):
        return self.rpc('tools/call', {'name': name, 'arguments': arguments})


if __name__ == '__main__':
    print(json.dumps(UnityMCP().call(sys.argv[1], **json.loads(sys.stdin.read().lstrip('\ufeff') or '{}')), ensure_ascii=False))
