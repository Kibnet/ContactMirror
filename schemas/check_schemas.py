"""Offline checks of the schema keywords emitted by generate_schemas.py and examples.

This is intentionally a check of this generated subset, not a general JSON Schema
implementation or a replacement for the application's state-aware validation.
Run: python schemas/generate_schemas.py --check; python schemas/check_schemas.py
"""
from __future__ import annotations
import copy
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def valid(value, schema, document) -> bool:
    if "$ref" in schema:
        target = document
        for token in schema["$ref"][2:].split("/"):
            target = target[token.replace("~1", "/").replace("~0", "~")]
        if not valid(value, target, document):
            return False
    if "not" in schema and valid(value, schema["not"], document):
        return False
    if "const" in schema and value != schema["const"]:
        return False
    if "enum" in schema and value not in schema["enum"]:
        return False
    if any(not valid(value, part, document) for part in schema.get("allOf", [])):
        return False
    if "anyOf" in schema and not any(valid(value, part, document) for part in schema["anyOf"]):
        return False
    if "if" in schema and valid(value, schema["if"], document) and not valid(value, schema.get("then", {}), document):
        return False
    numeric = isinstance(value, (int, float)) and not isinstance(value, bool)
    checks = {
        "object": isinstance(value, dict), "array": isinstance(value, list), "string": isinstance(value, str),
        "integer": numeric and int(value) == value, "number": numeric, "boolean": isinstance(value, bool), "null": value is None,
    }
    if "type" in schema and not any(checks[t] for t in ([schema["type"]] if isinstance(schema["type"], str) else schema["type"])):
        return False
    if isinstance(value, dict):
        if any(key not in value for key in schema.get("required", [])):
            return False
        properties = schema.get("properties", {})
        if schema.get("additionalProperties") is False and any(key not in properties for key in value):
            return False
        if any(not valid(item, properties[key], document) for key, item in value.items() if key in properties):
            return False
    if isinstance(value, list):
        if len(value) > schema.get("maxItems", float("inf")):
            return False
        if "items" in schema and any(not valid(item, schema["items"], document) for item in value):
            return False
        if schema.get("uniqueItems") and len({json.dumps(item, sort_keys=True) for item in value}) != len(value):
            return False
        if "contains" in schema:
            count = sum(valid(item, schema["contains"], document) for item in value)
            if not schema.get("minContains", 1) <= count <= schema.get("maxContains", float("inf")):
                return False
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0) or "pattern" in schema and not re.search(schema["pattern"], value):
            return False
    if numeric:
        if value < schema.get("minimum", -float("inf")) or value > schema.get("maximum", float("inf")):
            return False
        if "multipleOf" in schema and value % schema["multipleOf"] != 0:
            return False
    return True


def main() -> None:
    contact_schema = json.loads((ROOT / "schemas/contact.schema.json").read_text(encoding="utf-8"))
    group_schema = json.loads((ROOT / "schemas/group.schema.json").read_text(encoding="utf-8"))
    contact = json.loads(next((ROOT / "examples").glob("*.contact.json")).read_text(encoding="utf-8"))
    group = json.loads(next((ROOT / "examples").glob("*.group.json")).read_text(encoding="utf-8"))
    assert valid(contact, contact_schema, contact_schema), "Contact example rejected."
    assert valid(group, group_schema, group_schema), "Group example rejected."
    negatives = []
    for key, value in (("data", {"unknownCategory": []}), ("schemaVersion", 2), ("id", "00000000-0000-0000-0000-000000000000"),
                       ("labels", [group["id"], group["id"]]), ("photo", "photos/../secrets.txt")):
        mutated = copy.deepcopy(contact); mutated[key] = value; negatives.append(mutated)
    for names in ([{"givenName": "A", "futureUnknown": "value"}], [{"givenName": "A", "displayName": "output only"}],
                  [{"givenName": "A", "metadata": {"primary": True}}], [{"givenName": "A", "metadata": {"source": {"type": "PROFILE"}}}],
                  [{"givenName": "A"}, {"givenName": "B"}]):
        mutated = copy.deepcopy(contact); mutated["data"]["names"] = names; negatives.append(mutated)
    mutated = copy.deepcopy(contact)
    mutated["data"]["phoneNumbers"] = [{"value": "a", "metadata": {"sourcePrimary": True}}, {"value": "b", "metadata": {"sourcePrimary": True}}]
    negatives.append(mutated)
    mutated = copy.deepcopy(contact); mutated["data"]["birthdays"] = None; negatives.append(mutated)
    for date in ({"year": 0, "month": 2, "day": 30}, {"year": 1900, "month": 2, "day": 29}, {"year": 2026, "month": 4, "day": 31}, {"year": -1}):
        mutated = copy.deepcopy(contact); mutated["data"]["birthdays"] = [{"date": date}]; negatives.append(mutated)
    for index, mutated in enumerate(negatives):
        assert not valid(mutated, contact_schema, contact_schema), f"Negative contact fixture {index} was accepted."
    for data in ({"key": "missing-value"}, {"key": "a", "value": "b", "unknown": True}):
        mutated = copy.deepcopy(group); mutated["clientData"] = [data]
        assert not valid(mutated, group_schema, group_schema)
    for year in (0, 2000, 2024):
        mutated = copy.deepcopy(contact); mutated["data"]["birthdays"] = [{"date": {"year": year, "month": 2, "day": 29}}]
        assert valid(mutated, contact_schema, contact_schema)
    mutated = copy.deepcopy(contact)
    mutated["google"] = {"person": {"futureUnknown": {"raw": [1, 2]}, "metadata": {"sources": [{"type": "PROFILE"}]}}}
    assert valid(mutated, contact_schema, contact_schema)
    print(f"PASS: 2 synthetic examples, {len(negatives) + 2} negative fixtures, 4 extra positive fixtures using the emitted schema subset. Core runtime validation remains separate.")


if __name__ == "__main__":
    main()
