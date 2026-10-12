"""Dependency-free AirLLM Qwen3 Chat policy regression checks."""
import importlib.util
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
POLICY = ROOT / "distribution" / "airllm-runtime" / "chat_policy.py"
spec = importlib.util.spec_from_file_location("llmproxy_airllm_policy", POLICY)
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class FakeTokenizer:
    chat_template = "qwen3 jinja template"
    eos_token_id = 2
    unk_token_id = 0

    def __init__(self):
        self.last_kwargs = None

    def apply_chat_template(self, turns, **kwargs):
        self.last_kwargs = kwargs
        return "rendered-template"

    def convert_tokens_to_ids(self, token):
        return {"<|im_end|>": 11}.get(token, 0)


class AirLlmChatPolicyTests(unittest.TestCase):
    def test_qwen3_disable_thinking_is_explicit(self):
        tok = FakeTokenizer()
        turns = [{"role": "user", "content": "Reply with OK"}]
        self.assertEqual(policy.build_prompt(tok, turns, "Qwen/Qwen3-8B", False),
                         "rendered-template")
        self.assertFalse(tok.last_kwargs["enable_thinking"])

    def test_default_thinking_configuration_is_not_changed_by_adapter(self):
        tok = FakeTokenizer()
        policy.build_prompt(tok, [{"role": "user", "content": "hi"}],
                            "Qwen/Qwen3-8B", None)
        self.assertNotIn("enable_thinking", tok.last_kwargs)

    def test_unknown_model_cannot_silently_accept_qwen3_options(self):
        with self.assertRaisesRegex(ValueError, "only for Qwen3"):
            policy.build_prompt(FakeTokenizer(), [{"role": "user", "content": "hi"}],
                                "meta-llama/Llama-3-8B", False)

    def test_budget_exhaustion_reports_length_instead_of_stop(self):
        tok = FakeTokenizer()
        self.assertEqual(policy.finish_reason([311], 1, tok), "length")
        self.assertEqual(policy.finish_reason([9, 12, 14], 3, tok), "length")
        self.assertEqual(policy.finish_reason([23, 2], 2, tok), "stop")
        self.assertEqual(policy.finish_reason([33, 11], 2, tok), "stop")
        self.assertEqual(policy.finish_reason([88, 5], 8, tok), "stop")


if __name__ == "__main__":
    unittest.main()
