"""Every scaffold module imports without connecting to services."""

from importlib import import_module
from pkgutil import walk_packages

import ai_agent


def test_all_package_modules_import() -> None:
    names = [module.name for module in walk_packages(ai_agent.__path__, "ai_agent.")]
    assert "ai_agent.core.agent" in names
    assert "ai_agent.shared.protocols" in names
    assert "ai_agent.interfaces.api" in names
    for name in names:
        import_module(name)
