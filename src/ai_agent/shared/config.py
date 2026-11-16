"""Explicit configuration loading; importing this module has no side effects."""

import os
from collections.abc import Mapping

from pydantic import BaseModel, ConfigDict, Field, SecretStr


class Settings(BaseModel):
    """Foundation settings; service URLs and credentials are optional for tests.

    Environment variable names match uppercased field names. This does not
    read .env files or create workspaces. Service adapters must validate their
    required settings when implemented. Credentials must never be logged.
    """

    model_config = ConfigDict(hide_input_in_errors=True)

    database_url: SecretStr | None = None
    redis_url: SecretStr | None = None
    litellm_base_url: str | None = None
    litellm_api_key: SecretStr | None = None
    default_model: str | None = None
    telegram_bot_token: SecretStr | None = None
    agent_max_iterations: int = Field(default=20, ge=1)
    agent_workspace: str = "./workspace"

    @classmethod
    def from_env(cls, environ: Mapping[str, str] | None = None) -> "Settings":
        """Load only declared variables; blank optional service values are unset."""
        source = os.environ if environ is None else environ
        values = {
            name: source[name.upper()]
            for name in cls.model_fields
            if name.upper() in source
        }
        for name in (
            "database_url", "redis_url", "litellm_base_url", "litellm_api_key",
            "default_model", "telegram_bot_token",
        ):
            if values.get(name) == "":
                values.pop(name)
        return cls.model_validate(values)
