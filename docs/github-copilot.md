# GitHub Copilot integration

## Goal

GitHub Copilot is the developer experience while LlmProxy is the enterprise inference provider. Developers keep the Copilot experience in their supported IDE/CLI and the custom model endpoint points to LlmProxy.

```text
Developer
  -> GitHub Copilot
  -> https://<company-ai-domain>/v1
  -> LlmProxy
  -> logical model
  -> selected DGX deployment
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

For individual GitHub-user adoption and usage, GitHub exposes separate Copilot usage metrics that include user-oriented identifiers such as `user_id` and `user_login`. LlmProxy separately records infrastructure-level request telemetry associated with the API credential, logical model, DGX deployment and request timing.

The public custom-model/BYOK provider contract documents provider connection/authentication settings but does not promise a per-request developer email, GitHub user ID/login or Microsoft Entra object ID to the arbitrary OpenAI-compatible provider. LlmProxy must therefore not base authorization, billing or suspension on undocumented headers, source IP or User-Agent.

If real-time per-user inference attribution is required inside LlmProxy, use distinct personal LlmProxy credentials per user/client configuration, or place a trusted identity-aware intermediary in front of LlmProxy that injects a signed identity assertion under a contract we control.

A user disabled in the LlmProxy platform-user registry is blocked from the LlmProxy portal and all personal LlmProxy keys are revoked. That does not selectively block the same person when a GitHub organization is using one shared Copilot provider credential; individual Copilot access must be removed in GitHub or the integration must use per-user identity/credentials.

## Credential lifecycle

The admin console can create, rotate, revoke and reveal recoverable inference credentials. Authentication uses the HMAC-SHA256 hash; newly created/rotated credentials additionally store an application-encrypted recovery copy protected by the deployment `Authentication:ApiKeyPepper`.

Recommended production pattern:

1. Create a credential named for the integration, e.g. `GitHub Copilot Production`.
2. Copy the raw secret once into the GitHub custom-provider configuration.
3. Verify traffic in Request Metrics.
4. Revoke the bootstrap credential.
5. Rotate provider credentials periodically or immediately if exposure is suspected.
