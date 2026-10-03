# GitHub Copilot integration

## Goal

GitHub Copilot is the developer experience while LlmProxy is the enterprise inference provider. Developers keep the Copilot experience in their supported IDE/CLI and the custom model endpoint points to LlmProxy.

```text
Developer
  -> GitHub Copilot
  -> https://<company-ai-domain>/v1
  -> LlmProxy
  -> logical model
  -> selected inference node deployment
  -> vLLM
```

## Provider configuration

The provider-facing values are:

```text
Provider type: OpenAI-compatible
Base URL:       https://<company-ai-domain>/v1
API key:        lp_...
Model:          agic-code-fast
```

The exact Copilot administration screen and available custom-model capabilities depend on the GitHub plan and current GitHub rollout. Validate the final configuration against the organization/enterprise settings before production rollout.

## Required compatibility

The first integration gate validates:

- `GET /v1/models`;
- `POST /v1/chat/completions`;
- streaming SSE;
- tool/function calling payload preservation;
- cancellation;
- long-running requests;
- OpenAI-style errors;
- at least one real coding conversation from a supported Copilot client.

## Identity and usage

LlmProxy authenticates Copilot requests with the API credential supplied by the provider configuration. A central shared credential therefore identifies the **Copilot provider/workload**, not necessarily the individual GitHub user.

For individual GitHub-user adoption and usage, use GitHub Copilot administration/usage metrics. LlmProxy separately records infrastructure-level request telemetry associated with the API credential, logical model, inference node deployment and request timing.

If real-time per-user inference attribution is required inside LlmProxy, issue distinct provider credentials at a scope where the client configuration can select them and map those credentials to the desired person/team. Do not assume GitHub forwards an end-user login to an arbitrary OpenAI-compatible provider unless the specific integration contract documents it.

## Credential lifecycle

The admin console can create and revoke inference credentials. Raw secrets are shown only when created; the database stores an HMAC-SHA256 hash protected with the server-side `Authentication:ApiKeyPepper`.

Recommended production pattern:

1. Create a credential named for the integration, e.g. `GitHub Copilot Production`.
2. Copy the raw secret once into the GitHub custom-provider configuration.
3. Verify traffic in Request Metrics.
4. Revoke the bootstrap credential.
5. Rotate provider credentials periodically or immediately if exposure is suspected.
