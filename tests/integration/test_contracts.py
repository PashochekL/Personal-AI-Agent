"""Structural adapters exist only in tests; no application loop is implemented."""

import inspect
from typing import Any, get_type_hints

import pytest

from ai_agent.shared.models import (
    AgentResult, CommandResult, LLMResponse, Message, ToolDefinition, ToolResult,
)
from ai_agent.shared.protocols import Agent, ContextStore, LLMClient, SandboxExecutor, Tool


class StubLLM:
    async def complete(
        self, messages: list[Message], tools: list[ToolDefinition]
    ) -> LLMResponse:
        assert messages[0].role == "user"
        assert tools[0].name == "test.echo"
        return LLMResponse(content="stub", finish_reason="stop")


class StubTool:
    name: str = "test.echo"
    description: str = "Echo for contract tests"
    parameters: dict[str, Any] = {"type": "object"}

    async def execute(self, arguments: dict[str, Any]) -> ToolResult:
        return ToolResult(success=True, content=str(arguments["text"]))


class StubStore:
    def __init__(self) -> None:
        self.messages: dict[str, list[Message]] = {}

    async def get_messages(self, session_id: str) -> list[Message]:
        return list(self.messages.get(session_id, []))

    async def append_message(self, session_id: str, message: Message) -> None:
        self.messages.setdefault(session_id, []).append(message)

    async def replace_messages(self, session_id: str, messages: list[Message]) -> None:
        self.messages[session_id] = list(messages)


class StubSandbox:
    async def execute(
        self, command: str, workspace: str, timeout: int = 30
    ) -> CommandResult:
        assert command == "contract-test"
        assert workspace == "./workspace"
        assert timeout == 30
        return CommandResult(exit_code=0, stdout="stub")


class StubAgent:
    async def run(self, session_id: str, user_message: str) -> AgentResult:
        return AgentResult(session_id=session_id, response=user_message, status="COMPLETED")


@pytest.mark.parametrize(("protocol", "adapter", "methods"), [
    (LLMClient, StubLLM, ["complete"]),
    (Tool, StubTool, ["execute"]),
    (ContextStore, StubStore, ["get_messages", "append_message", "replace_messages"]),
    (SandboxExecutor, StubSandbox, ["execute"]),
    (Agent, StubAgent, ["run"]),
])
def test_adapter_matches_async_protocol(protocol: type, adapter: type, methods: list[str]) -> None:
    assert isinstance(adapter(), protocol)
    for name in methods:
        expected = getattr(protocol, name)
        actual = getattr(adapter, name)
        assert inspect.iscoroutinefunction(actual)
        assert inspect.signature(expected) == inspect.signature(actual)
        assert get_type_hints(expected) == get_type_hints(actual)


async def test_provider_independent_handoffs() -> None:
    store: ContextStore = StubStore()
    gateway: LLMClient = StubLLM()
    tool: Tool = StubTool()
    sandbox: SandboxExecutor = StubSandbox()
    agent: Agent = StubAgent()

    await store.append_message("s1", Message(role="user", content="hello"))
    messages = await store.get_messages("s1")
    response = await gateway.complete(messages, [ToolDefinition(
        name=tool.name, description=tool.description, parameters=tool.parameters
    )])
    assert response.content == "stub"
    assert (await tool.execute({"text": "hello"})).content == "hello"
    assert (await sandbox.execute("contract-test", "./workspace")).exit_code == 0

    await store.replace_messages("s1", [Message(role="system", content="summary")])
    assert (await store.get_messages("s1"))[0].content == "summary"
    assert await store.get_messages("s2") == []
    assert (await agent.run("s1", "CLI")).session_id == "s1"
    assert (await agent.run("s1", "Telegram")).session_id == "s1"
