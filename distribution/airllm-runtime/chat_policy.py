"""Small, dependency-free Chat template and OpenAI finish-reason helpers.

Qwen3 uses thinking mode by default; a diagnostic with a small token budget
should explicitly ask for enable_thinking=False rather than generating only
the beginning of <think>. Other model families preserve their native template.
"""


def is_qwen3(model_id: str) -> bool:
    return model_id.rsplit("/", 1)[-1].lower().startswith("qwen3-")


def build_prompt(tokenizer, turns, model_id: str, thinking: bool | None):
    if thinking is not None and not is_qwen3(model_id):
        raise ValueError("enable_thinking is supported only for Qwen3 chat templates")
    if not getattr(tokenizer, "chat_template", None):
        if thinking is not None:
            raise ValueError("This tokenizer has no Chat template with thinking controls")
        return "\n".join(f"{m['role']}: {m['content']}" for m in turns) + "\nassistant:"
    kwargs = {"tokenize": False, "add_generation_prompt": True}
    if thinking is not None:
        kwargs["enable_thinking"] = thinking
    return tokenizer.apply_chat_template(turns, **kwargs)


def finish_reason(generated_ids, max_new_tokens: int, tokenizer) -> str:
    # A budget-exhausted generation is NOT an EOS stop. This makes 1-token
    # acceptance tests report 'length' instead of a misleading 'stop'.
    if not generated_ids:
        return "stop"
    eos = tokenizer.eos_token_id
    ends = set(eos) if isinstance(eos, (list, tuple, set)) else {eos}
    if hasattr(tokenizer, "convert_tokens_to_ids"):
        token = tokenizer.convert_tokens_to_ids("<|im_end|>")
        if isinstance(token, int) and token != getattr(tokenizer, "unk_token_id", None):
            ends.add(token)
    if generated_ids[-1] in ends:
        return "stop"
    return "length" if len(generated_ids) >= max_new_tokens else "stop"
