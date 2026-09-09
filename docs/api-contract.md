# API Contract

## Compatibility goal

The public inference API follows the OpenAI-compatible shape required by GitHub Copilot custom model/BYOK integrations. Compatibility is treated as a product contract, not as an implementation detail.

## `GET /v1/models`

Returns logical models currently enabled for clients.

Example:

```json
{
  "object": "list",
  "data": [
    {
      "id": "agic-code-fast",
      "object": "model",
      "created": 0,
      "owned_by": "llmproxy"
    }
  ]
}
```

## `POST /v1/chat/completions`

The gateway accepts an OpenAI-style request. The incoming logical `model` value is rewritten to the provider model configured on the selected deployment before forwarding the request to vLLM.

Example request:

```json
{
  "model": "agic-code-fast",
  "messages": [
    { "role": "user", "content": "Explain this method" }
  ],
  "stream": true
}
```

The gateway must preserve unknown compatible fields rather than deserializing into a restrictive DTO. This is important for tool calling and future OpenAI-compatible fields.

## Errors

Gateway-generated errors use an OpenAI-like envelope:

```json
{
  "error": {
    "message": "No healthy deployment is available for model 'agic-code-fast'.",
    "type": "gateway_unavailable",
    "code": "no_healthy_deployment"
  }
}
```

## Authentication

Inference endpoints require:

```http
Authorization: Bearer <gateway-api-key>
```

The first bootstrap release supports one environment-provided credential. The product backlog replaces it with database-backed, hashed, revocable credentials with expiry and allowed-model policies.

## Administration

Administration endpoints live under `/api/admin`. They are not OpenAI-compatible and may evolve independently of `/v1`.
