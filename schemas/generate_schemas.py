"""Regenerate the v1 editor schemas from the pinned Google discovery and Core registry.

Run: python schemas/generate_schemas.py
No external dependencies, network calls or package installation.
"""
from __future__ import annotations

import copy
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DISCOVERY = json.loads((ROOT / "src/ContactMirror.Core/people-schema.json").read_text(encoding="utf-8-sig"))
SOURCE = (ROOT / "src/ContactMirror.Core/CapabilityRegistry.cs").read_text(encoding="utf-8-sig")
FIELDS = re.findall(r'"([A-Za-z]+)"', re.search(r'WritableFields\s*=\s*\[(.*?)\];', SOURCE, re.S).group(1))
assert len(FIELDS) == 23 and len(set(FIELDS)) == 23
GOOGLE = DISCOVERY["schemas"]
UUID = {
    "type": "string", "format": "uuid",
    "pattern": r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
    "not": {"const": "00000000-0000-0000-0000-000000000000"},
}


def definitions(fields: list[str]) -> tuple[dict, dict]:
    defs: dict[str, dict] = {}

    def convert(node: dict) -> dict:
        if "$ref" in node:
            name = node["$ref"]
            if name not in defs:
                defs[name] = {}  # Reserve before recursive traversal.
                defs[name] = convert(GOOGLE[name])
                if name == "FieldMetadata":
                    defs[name]["properties"] = {"sourcePrimary": {"type": "boolean"}}
                if name == "Date":
                    defs[name]["properties"] = {
                        "year": {"type": "integer", "minimum": 0, "maximum": 9999},
                        "month": {"type": "integer", "minimum": 0, "maximum": 12},
                        "day": {"type": "integer", "minimum": 0, "maximum": 31},
                    }
                    defs[name]["allOf"] = [
                        {"if": {"required": ["month"], "properties": {"month": {"enum": [4, 6, 9, 11]}}},
                         "then": {"properties": {"day": {"maximum": 30}}}},
                        {"if": {"required": ["month"], "properties": {"month": {"const": 2}}},
                         "then": {"properties": {"day": {"maximum": 29}}}},
                        {"if": {"required": ["month", "day"], "properties": {"month": {"const": 2}, "day": {"const": 29}}},
                         "then": {"properties": {"year": {"anyOf": [
                             {"const": 0}, {"multipleOf": 400},
                             {"allOf": [{"multipleOf": 4}, {"not": {"multipleOf": 100}}]},
                         ]}}}},
                    ]
            result = {"$ref": "#/$defs/" + name}
            if "description" in node:
                result["description"] = node["description"]
            return result
        result = {key: copy.deepcopy(node[key]) for key in ("type", "description", "enum") if key in node}
        if node.get("format") == "int32":
            result.update(minimum=-2147483648, maximum=2147483647)
        if node.get("type") == "object":
            result["additionalProperties"] = False
            result["properties"] = {
                key: convert(value) for key, value in node.get("properties", {}).items()
                if not value.get("readOnly") and (node.get("id") != "FieldMetadata" or key == "sourcePrimary")
            }
        if "items" in node:
            result["items"] = convert(node["items"])
        return result

    properties = {field: convert(GOOGLE["Person"]["properties"][field]) for field in fields}
    primary = {
        "type": "object", "required": ["metadata"],
        "properties": {"metadata": {"type": "object", "required": ["sourcePrimary"], "properties": {"sourcePrimary": {"const": True}}}},
    }
    for field, schema in properties.items():
        schema.update(contains=copy.deepcopy(primary), minContains=0, maxContains=1)
        if field in ("names", "biographies", "birthdays", "genders"):
            schema["maxItems"] = 1
    # Output metadata Source is never reachable from editable schemas.
    assert "Source" not in defs
    return properties, defs


def envelope(kind: str) -> dict:
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": f"urn:contactmirror:schema:{kind}:v1",
        "title": "ContactMirror v1 — " + ("контакт" if kind == "contact" else "ярлык"),
        "$comment": "Derived from pinned Google People discovery revision " + DISCOVERY["revision"] + ". Runtime validation additionally checks workspace references, account binding, unknown remote replacement fields and immutable snapshots.",
        "type": "object", "additionalProperties": False,
        "properties": {
            "schemaVersion": {"const": 1, "type": "integer"},
            "id": copy.deepcopy(UUID),
            "extensions": {"type": "object", "additionalProperties": True, "description": "Локальные расширения. Не отправляются в Google."},
            "google": {"type": ["object", "null"], "additionalProperties": True, "readOnly": True,
                       "description": "Полный исходный снимок Google, включая неизвестные и вычисленные поля. Изменять его нельзя; это проверяет приложение при сравнении с baseline."},
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Verify generated files without writing them.")
    args = parser.parse_args()
    properties, defs = definitions(FIELDS)
    contact = envelope("contact")
    contact["required"] = ["schemaVersion", "id", "data"]
    contact["properties"].update({
        "data": {"type": "object", "additionalProperties": False, "properties": properties,
                 "description": "Редактируемые CONTACT-поля. Массив [] явно очищает категорию; null недопустим."},
        "labels": {"type": "array", "items": copy.deepcopy(UUID), "uniqueItems": True,
                   "description": "UUID пользовательских ярлыков этой папки. Наличие ярлыка и совпадение UUID без учёта регистра проверяет приложение."},
        "starred": {"type": "boolean"},
        "photo": {"type": ["string", "null"], "pattern": r'^photos/(?!\.\.(?:/|$))(?!.*\/\.\.(?:/|$))[^\\:\x00-\x1f]+$',
                  "description": "Относительный путь внутри photos/ либо null для удаления фото. Наличие, JPEG/PNG, размер, ссылки и безопасный путь проверяет приложение."},
    })
    contact["$defs"] = defs
    group = envelope("group")
    group["required"] = ["schemaVersion", "id", "name"]
    group["properties"].update({
        "name": {"type": "string", "minLength": 1, "pattern": r"\S", "description": "Непустое имя пользовательского ярлыка."},
        "clientData": {"type": "array", "items": {"type": GOOGLE["GroupClientData"]["type"], "required": ["key", "value"], "additionalProperties": False,
                      "properties": {key: {"type": value["type"], "description": value.get("description", "")} for key, value in GOOGLE["GroupClientData"]["properties"].items()}}},
    })
    for name, schema in (("contact", contact), ("group", group)):
        path = ROOT / "schemas" / f"{name}.schema.json"
        if args.check:
            assert json.loads(path.read_text(encoding="utf-8")) == schema, f"{path.name} is stale or changed; regenerate."
        else:
            path.write_text(json.dumps(schema, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{'Verified' if args.check else 'Generated'} draft 2020-12 contact/group schemas: {len(FIELDS)} writable categories; {len(defs)} reachable editable definitions; discovery {DISCOVERY['revision']}.")


if __name__ == "__main__":
    main()
