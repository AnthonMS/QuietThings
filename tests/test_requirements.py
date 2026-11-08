"""Check dependencies against the constraints HA applies during installation."""

import json
from pathlib import Path

import homeassistant
from packaging.requirements import Requirement
from packaging.utils import canonicalize_name


def test_manifest_matches_core_package_constraints():
    manifest_path = (
        Path(__file__).parents[1] / "custom_components/quietthings/manifest.json"
    )
    manifest = json.loads(manifest_path.read_text())
    constraints = {}
    constraints_path = Path(homeassistant.__file__).parent / "package_constraints.txt"
    for line in constraints_path.read_text().splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        requirement = Requirement(line)
        for spec in requirement.specifier:
            if spec.operator == "==" and "*" not in spec.version:
                constraints[canonicalize_name(requirement.name)] = spec.version
    for value in manifest["requirements"]:
        requirement = Requirement(value)
        if version := constraints.get(canonicalize_name(requirement.name)):
            assert requirement.specifier.contains(version), (
                f"{value} conflicts with HA constraint {version}"
            )
