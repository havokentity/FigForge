// =============================================================================
// HTTP leader hardening tests. These exercise the shared body reader directly so
// they do not depend on the fixed local bridge port being available.
// =============================================================================

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { PassThrough } from 'node:stream';

import { readRpcBody, RpcBodyTooLargeError } from '../dist/leader.js';

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
