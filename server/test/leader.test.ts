// =============================================================================
// HTTP leader hardening tests. These exercise the shared body reader directly so
// they do not depend on the fixed local bridge port being available.
// =============================================================================

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { PassThrough } from 'node:stream';

import { readRpcBody, RpcBodyTooLargeError } from '../dist/leader.js';
import { rpcRequestSchema } from '../src/schema.ts';

function requestStream(headers: http.IncomingHttpHeaders = {}): http.IncomingMessage {
  const req = new PassThrough() as http.IncomingMessage;
  req.headers = headers;
  return req;
}

describe('readRpcBody', () => {
  it('reads a request body within the configured byte limit', async () => {
    const req = requestStream();
    const body = readRpcBody(req, 10);

    req.write('{"a"');
    req.end(':1}');

    assert.equal(await body, '{"a":1}');
  });

  it('rejects a declared body that exceeds the configured byte limit', async () => {
    const req = requestStream({ 'content-length': '11' });

    await assert.rejects(
      readRpcBody(req, 10),
      (error) => error instanceof RpcBodyTooLargeError && error.message === 'RPC body exceeds 10 bytes.',
    );
  });

  it('rejects a streamed body once it exceeds the configured byte limit', async () => {
    const req = requestStream();
    const body = readRpcBody(req, 10);

    req.write('12345');
    req.write('67890');
    req.end('x');

    await assert.rejects(
      body,
      (error) => error instanceof RpcBodyTooLargeError && error.message === 'RPC body exceeds 10 bytes.',
    );
  });
});

describe('rpcRequestSchema', () => {
  it('accepts an allow-listed tool with well-formed node ids and params', () => {
    const r = rpcRequestSchema.safeParse({
      tool: 'get_node',
      nodeIds: ['123:456', 'I123:4;567:8'],
      params: { nodeId: '123:456' },
    });
    assert.equal(r.success, true);
  });

  it('accepts a bare tool with no nodeIds or params', () => {
    assert.equal(rpcRequestSchema.safeParse({ tool: 'get_metadata' }).success, true);
  });

  it('strips unknown envelope keys rather than failing', () => {
    const r = rpcRequestSchema.safeParse({ tool: 'get_document', requestId: 'x', extra: 1 });
    assert.equal(r.success, true);
    if (r.success) assert.deepEqual(Object.keys(r.data), ['tool']);
  });

  it('rejects a tool name that is not allow-listed', () => {
    assert.equal(rpcRequestSchema.safeParse({ tool: 'rm_rf', params: {} }).success, false);
    // a real MCP tool that is NOT a bridge message type must also be rejected
    assert.equal(rpcRequestSchema.safeParse({ tool: 'save_screenshots' }).success, false);
  });

  it('rejects a missing or non-string tool', () => {
    assert.equal(rpcRequestSchema.safeParse({}).success, false);
    assert.equal(rpcRequestSchema.safeParse({ tool: 42 }).success, false);
  });

  it('rejects malformed node ids and non-array nodeIds', () => {
    assert.equal(rpcRequestSchema.safeParse({ tool: 'get_node', nodeIds: ['123-456'] }).success, false);
    assert.equal(rpcRequestSchema.safeParse({ tool: 'get_node', nodeIds: '123:456' }).success, false);
  });

  it('rejects a non-object body', () => {
    assert.equal(rpcRequestSchema.safeParse('get_metadata').success, false);
    assert.equal(rpcRequestSchema.safeParse(null).success, false);
  });
});
