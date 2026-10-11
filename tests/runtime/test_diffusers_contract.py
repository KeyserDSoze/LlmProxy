"""No GPU, PyTorch or third-party package required."""
import importlib.util
import pathlib
import struct
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
PATH = ROOT / "distribution" / "diffusers-runtime" / "contract.py"
spec = importlib.util.spec_from_file_location("llmproxy_diffusers_contract", PATH)
contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(contract)

DEFAULTS = dict(model=contract.MODEL_ID, prompt="a mountain",
                size="512x512", n=1, quality="standard",
                response_format="b64_json", steps=20,
                negative_prompt=None, seed=None)


class DiffusersContractTests(unittest.TestCase):
    def test_only_curated_image_models_and_safe_parameters(self):
        contract.validate_parameters(**DEFAULTS)
        for field, bad in {
            "model": "untrusted/remote-code",
            "prompt": "",
            "size": "4096x4096",
            "n": 3,
            "quality": "hd",
            "response_format": "url",
            "steps": 100,
            "negative_prompt": "x" * 1001,
            "seed": -1,
        }.items():
            with self.subTest(field=field), self.assertRaises((ValueError, LookupError)):
                contract.validate_parameters(**(DEFAULTS | {field: bad}))

    def test_png_header_and_exact_dimensions_are_mandatory(self):
        # A structural PNG fixture: sufficient to validate admission metadata,
        # not a semantic image decode. Real GPU smoke tests decode complete PNGs.
        png = (contract.PNG_HEADER + b"\x00\x00\x00\x0dIHDR"
               + struct.pack(">II", 512, 512) + bytes([8, 2, 0, 0, 0]))
        self.assertTrue(contract.png_matches_request(png, 512, 512))
        self.assertFalse(contract.png_matches_request(png, 768, 768))
        self.assertFalse(contract.png_matches_request(b"not an image", 512, 512))
        self.assertFalse(contract.png_matches_request(png + b"x" * (8 * 1024 * 1024), 512, 512))


if __name__ == "__main__":
    unittest.main()
