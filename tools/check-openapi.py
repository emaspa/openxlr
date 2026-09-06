#!/usr/bin/env python3
"""Structural check of docs/openapi-v1.json with the standard library only.

Not a full validator: it checks the shape a client generator relies on
(version, info, servers, every path's methods with responses, every $ref
resolving to a component, every security requirement naming a declared
scheme) and fails loudly on drift.
"""
import json
import re
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "docs/openapi-v1.json"
doc = json.load(open(path))
errors = []
METHODS = {"get", "put", "post", "delete", "options", "head", "patch", "trace"}


def ref_ok(ref):
    m = re.fullmatch(r"#/components/([A-Za-z]+)/([A-Za-z0-9_.-]+)", ref)
    return bool(m) and m.group(2) in doc.get("components", {}).get(m.group(1), {})


def walk(node, where):
    if isinstance(node, dict):
        if "$ref" in node and not ref_ok(node["$ref"]):
            errors.append(f"{where}: unresolved $ref {node['$ref']}")
        for k, v in node.items():
            walk(v, f"{where}/{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            walk(v, f"{where}[{i}]")


if not str(doc.get("openapi", "")).startswith("3."):
    errors.append("openapi version must be 3.x")
for key in ("title", "version"):
    if not doc.get("info", {}).get(key):
        errors.append(f"info.{key} missing")
if not doc.get("servers"):
    errors.append("servers missing")
schemes = doc.get("components", {}).get("securitySchemes", {})
for req in doc.get("security", []):
    for name in req:
        if name not in schemes:
            errors.append(f"global security names undeclared scheme {name}")
paths = doc.get("paths", {})
if not paths:
    errors.append("no paths")
for p, item in paths.items():
    if not p.startswith("/"):
        errors.append(f"path {p} must start with /")
    ops = {k: v for k, v in item.items() if k in METHODS}
    if not ops:
        errors.append(f"{p}: no operations")
    for method, op in ops.items():
        responses = op.get("responses", {})
        if not responses:
            errors.append(f"{method.upper()} {p}: no responses")
        for code in responses:
            if not re.fullmatch(r"[1-5]XX|[1-5][0-9][0-9]|default", str(code)):
                errors.append(f"{method.upper()} {p}: bad response code {code}")
        for req in op.get("security", []):
            for name in req:
                if name not in schemes:
                    errors.append(f"{method.upper()} {p}: security names undeclared scheme {name}")
walk(doc, "")
for e in errors:
    print(f"openapi: {e}", file=sys.stderr)
print(f"openapi: {len(paths)} paths, {sum(len([k for k in i if k in METHODS]) for i in paths.values())} operations, "
      f"{len(schemes)} security schemes, {'ok' if not errors else str(len(errors)) + ' problems'}")
sys.exit(1 if errors else 0)
