"""Async structural interfaces; implementations live in their owning modules."""

from typing import Any, Protocol, runtime_checkable

from ai_agent.shared.models import (
    AgentResult,
    CommandResult,
    LLMResponse,
    Message,
    ToolDefinition,
    ToolResult,
)


@runtime_checkable
class LLMClient(Protocol):
    async def complete(
        self, messages: list[Message], tools: list[ToolDefinition]
    ) -> LLMResponse: ...


@runtime_checkable
class Tool(Protocol):
    name: str
    description: str
    parameters: dict[str, Any]

    async def execute(self, arguments: dict[str, Any]) -> ToolResult: ...


@runtime_checkable
class ContextStore(Protocol):
    async def get_messages(self, session_id: str) -> list[Message]: ...

    async def append_message(self, session_id: str, message: Message) -> None: ...

    async def replace_messages(
        self, session_id: str, messages: list[Message]
    ) -> None: ...


@runtime_checkable
class SandboxExecutor(Protocol):
    async def execute(
        self, command: str, workspace: str, timeout: int = 30
    ) -> CommandResult: ...


@runtime_checkable
class Agent(Protocol):
    async def run(self, session_id: str, user_message: str) -> AgentResult: ...
