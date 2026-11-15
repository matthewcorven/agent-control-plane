import http from 'node:http';

let speech = [];
http.createServer(async (req, res) => {
  if (req.url === '/state') {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify(speech));
  }
  const parts = [];
  for await (const part of req) parts.push(part);
  const body = Buffer.concat(parts);
  if (req.method === 'POST' && req.url === '/v1/audio/transcriptions') {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ text: 'audio pipeline canary' }));
  }
  if (req.method === 'POST' && req.url === '/v1/audio/speech') {
    speech.push(JSON.parse(body.toString()));
    const wav = Buffer.alloc(44);
    wav.write('RIFF', 0); wav.write('WAVE', 8);
    res.writeHead(200, { 'content-type': 'audio/wav' });
    return res.end(wav);
  }
  res.writeHead(404); res.end();
}).listen(5399, '127.0.0.1');
