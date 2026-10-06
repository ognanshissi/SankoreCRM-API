#!/usr/bin/env python3
"""Rewrites an OpenAPI 3.1 document into the 3.0 dialect NSwag can generate from.

Why this exists
---------------
FastAPI emits 3.1, where an optional value is `anyOf: [{...}, {"type": "null"}]`.
NJsonSchema v11 (inside NSwag 14.7) does not understand that union: it generates an EMPTY
marker class per occurrence — `class Birth_date { AdditionalProperties }` — so the value is
unreachable from C#. It hits every nullable field, including `OcrResponse.mrz` (a whole
object) and `FieldValue.value` (the extracted field itself).

So the checked-in document the build generates from is the 3.0 equivalent:
`anyOf: [T, null]` -> T + `nullable: true`, and a nullable `$ref` -> `allOf: [$ref]` +
`nullable: true`, which is how 3.0 spells it.

Usage
-----
    python3 tools/normalize-openapi-nullable.py <source 3.1 document> <output 3.0 document>

Run it whenever the biometry team ships a new document, then rebuild: if somebody forgets,
the build FAILS on the mappers in HttpBiometryClient rather than silently producing a client
that cannot read a date of birth.
"""
import json
import sys


def normalize(node):
    if isinstance(node, list):
        return [normalize(item) for item in node]

    if not isinstance(node, dict):
        return node

    variants = node.get("anyOf")

    if isinstance(variants, list):
        non_null = [v for v in variants if v.get("type") != "null"]
        has_null = len(non_null) != len(variants)

        # Only the two-branch "T or null" shape is rewritten. A real union of several types has
        # no 3.0 equivalent, so it is left alone and NSwag's own handling applies.
        if has_null and len(non_null) == 1:
            siblings = {k: normalize(v) for k, v in node.items() if k != "anyOf"}
            single = normalize(non_null[0])

            if "$ref" in single:
                return {**siblings, "allOf": [single], "nullable": True}

            return {**siblings, **single, "nullable": True}

    return {k: normalize(v) for k, v in node.items()}


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    with open(sys.argv[1], encoding="utf-8") as f:
        document = json.load(f)

    document = normalize(document)

    # 3.0 is the dialect `nullable` belongs to; left at 3.1 the keyword is simply ignored.
    document["openapi"] = "3.0.3"

    with open(sys.argv[2], "w", encoding="utf-8") as f:
        json.dump(document, f, indent=2, ensure_ascii=False)
        f.write("\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
