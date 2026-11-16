"""Configuration can be used in tests without secrets or external services."""

from pathlib import Path

import pytest
from pydantic import ValidationError

from ai_agent.shared.config import Settings


def test_defaults_without_service_configuration() -> None:
    settings = Settings.from_env({})
    assert settings.agent_max_iterations == 20
    assert settings.agent_workspace == "./workspace"
    assert settings.database_url is None


def test_env_example_can_be_loaded_without_real_secrets() -> None:
    lines = (Path(__file__).resolve().parents[2] / ".env.example").read_text().splitlines()
    environ = dict(line.split("=", 1) for line in lines if line and not line.startswith("#"))
    settings = Settings.from_env(environ)
    assert settings.agent_max_iterations == 20
    assert settings.litellm_api_key is None
    assert settings.telegram_bot_token is None


def test_explicit_environment_conversion_and_secret_masking() -> None:
    settings = Settings.from_env({
        "AGENT_MAX_ITERATIONS": "5", "AGENT_WORKSPACE": "./test-workspace",
        "DATABASE_URL": "postgresql://test:example@localhost/db",
        "REDIS_URL": "redis://:example@localhost/0",
        "LITELLM_API_KEY": "example-api-key", "TELEGRAM_BOT_TOKEN": "example-bot-token",
        "UNRELATED": "ignored",
    })
    assert settings.agent_max_iterations == 5
    assert settings.agent_workspace == "./test-workspace"
    assert settings.litellm_api_key is not None
    assert settings.litellm_api_key.get_secret_value() == "example-api-key"
    assert "example" not in repr(settings)
    assert "example" not in settings.model_dump_json()


def test_process_environment_is_read_only_when_requested(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("AGENT_MAX_ITERATIONS", "7")
    assert Settings.from_env().agent_max_iterations == 7
    assert Settings().agent_max_iterations == 20


@pytest.mark.parametrize("value", ["0", "-1", "invalid", ""])
def test_invalid_iteration_limit_is_rejected(value: str) -> None:
    with pytest.raises(ValidationError):
        Settings.from_env({"AGENT_MAX_ITERATIONS": value})
