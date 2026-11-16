"""Validate shared payloads without providers, databases or agent execution."""

from typing import Any

import pytest
from pydantic import BaseModel, ValidationError

from ai_agent.shared.models import (
    AgentResult, CommandResult, LLMResponse, Message, Session, Task, TaskStatus,
    TokenUsage, ToolCall, ToolDefinition, ToolResult,
)


@pytest.mark.parametrize("role", ["system", "user", "assistant", "tool"])
def test_message_roles(role: str) -> None:
    payload = {"role": role, "content": "text"}
    if role == "tool":
        payload["tool_call_id"] = "call-1"
    assert Message.model_validate(payload).role == role


def test_invalid_role_is_rejected() -> None:
    with pytest.raises(ValidationError):
        Message.model_validate({"role": "developer", "content": "text"})


@pytest.mark.parametrize("reference", [None, ""])
def test_tool_reply_requires_call_reference(reference: str | None) -> None:
    with pytest.raises(ValidationError, match="tool_call_id"):
        Message(role="tool", content="result", tool_call_id=reference)


def test_multiple_tool_calls_and_replies_survive_history_serialization() -> None:
    calls = [
        ToolCall(id="call-1", name="filesystem.read_file", arguments={"path": "a.txt"}),
        ToolCall(id="call-2", name="web.search", arguments={"query": "Python"}),
    ]
    messages = [Message(role="assistant", tool_calls=calls)]
    messages.extend(
        Message(role="tool", content="result", tool_call_id=call.id) for call in calls
    )
    restored = [Message.model_validate_json(item.model_dump_json()) for item in messages]
    assert restored == messages
    assert restored[0].tool_calls[0].arguments == {"path": "a.txt"}
    assert [message.tool_call_id for message in restored[1:]] == [call.id for call in calls]


def test_tool_payload_and_definition_do_not_depend_on_a_provider() -> None:
    definition = ToolDefinition(
        name="web.search", description="Search", parameters={"type": "object"}
    )
    result = ToolResult(success=False, content="", error="Unavailable")
    assert definition.parameters == {"type": "object"}
    assert result.model_dump() == {"success": False, "content": "", "error": "Unavailable"}


def test_empty_and_tool_only_llm_responses() -> None:
    first = LLMResponse()
    second = LLMResponse()
    first.tool_calls.append(ToolCall(id="1", name="test", arguments={}))
    assert second.tool_calls == []
    assert first.content is None
    assert first.usage is None
    assert Message(role="user", content="hello").tool_calls == []


def test_reported_zero_tokens_and_unreported_tokens_are_distinct() -> None:
    response = LLMResponse(usage=TokenUsage(input_tokens=0, output_tokens=3))
    restored = LLMResponse.model_validate_json(response.model_dump_json())
    assert restored.usage is not None
    assert restored.usage.input_tokens == 0
    assert restored.usage.total_tokens is None
    with pytest.raises(ValidationError):
        TokenUsage(input_tokens=-1)


def test_sandbox_timeout_does_not_invent_an_exit_code() -> None:
    result = CommandResult(exit_code=None, stderr="timeout", timed_out=True)
    assert CommandResult.model_validate_json(result.model_dump_json()) == result


def test_pending_task_preserves_required_request() -> None:
    session = Session(session_id="session-1")
    task = Task(task_id="task-1", session_id=session.session_id, user_message="Привет")
    assert task.status is TaskStatus.PENDING
    assert task.result is None
    assert Task.model_validate_json(task.model_dump_json()).user_message == "Привет"
    assert {state.value for state in TaskStatus} == {
        "PENDING", "RUNNING", "COMPLETED", "FAILED", "CANCELLED"
    }
    with pytest.raises(ValidationError):
        Task(task_id="task-2", session_id=session.session_id, user_message="hi", status="UNKNOWN")


@pytest.mark.parametrize("status", list(TaskStatus))
def test_task_json_round_trip_preserves_request_status_and_result(status: TaskStatus) -> None:
    result = None
    if status is TaskStatus.COMPLETED:
        result = AgentResult(session_id="s1", status="COMPLETED", response="answer")
    elif status is TaskStatus.FAILED:
        result = AgentResult(session_id="s1", status="FAILED", error="iteration limit reached")
    task = Task(
        task_id="t1", session_id="s1", user_message="  Запрос пользователя\n",
        status=status, result=result,
    )
    payload = task.model_dump(mode="json")
    assert payload["status"] == status.value
    if result is not None:
        assert payload["result"]["status"] == status.value
    restored = Task.model_validate_json(task.model_dump_json())
    assert restored == task
    assert restored.user_message == "  Запрос пользователя\n"


def test_task_requires_user_message() -> None:
    with pytest.raises(ValidationError, match="user_message"):
        Task.model_validate({"task_id": "t1", "session_id": "s1"})


@pytest.mark.parametrize("status", ["COMPLETED", "FAILED", "CANCELLED"])
def test_agent_terminal_statuses_serialize(status: str) -> None:
    result = AgentResult.model_validate({
        "session_id": "s1", "status": status,
        "response": "answer" if status == "COMPLETED" else None,
        "error": "unavailable" if status == "FAILED" else None,
    })
    assert result.model_dump(mode="json")["status"] == status
    assert AgentResult.model_validate_json(result.model_dump_json()) == result


def test_task_enum_can_be_used_for_agent_terminal_status() -> None:
    result = AgentResult(session_id="s1", status=TaskStatus.COMPLETED, response="answer")
    assert result.model_dump(mode="json")["status"] == "COMPLETED"


@pytest.mark.parametrize("status", ["PENDING", "RUNNING", "limit_reached", "completed", "UNKNOWN", ""])
def test_agent_rejects_nonterminal_and_unsupported_statuses(status: str) -> None:
    with pytest.raises(ValidationError, match="status"):
        AgentResult.model_validate({"session_id": "s1", "status": status, "response": "answer"})


@pytest.mark.parametrize("response", [None, "", " \t\n"])
def test_completed_agent_requires_a_real_response(response: str | None) -> None:
    with pytest.raises(ValidationError, match="response"):
        AgentResult(session_id="s1", status="COMPLETED", response=response)


def test_completed_agent_cannot_report_an_error() -> None:
    with pytest.raises(ValidationError, match="error"):
        AgentResult(session_id="s1", status="COMPLETED", response="answer", error="failed")


@pytest.mark.parametrize("error", [None, "", " \t\n"])
def test_failed_agent_requires_a_reason(error: str | None) -> None:
    with pytest.raises(ValidationError):
        AgentResult(session_id="s1", status="FAILED", error=error)


def test_failed_agent_can_preserve_partial_output() -> None:
    result = AgentResult(
        session_id="s1", status="FAILED", response="partial answer", error="iteration limit reached"
    )
    assert AgentResult.model_validate_json(result.model_dump_json()) == result


@pytest.mark.parametrize("task_status", list(TaskStatus))
@pytest.mark.parametrize("result_status", ["COMPLETED", "FAILED", "CANCELLED"])
def test_task_and_result_statuses_must_match(task_status: TaskStatus, result_status: str) -> None:
    payload = {
        "task_id": "t1", "session_id": "s1", "user_message": "hello", "status": task_status,
        "result": {
            "session_id": "s1", "status": result_status,
            "response": "answer" if result_status == "COMPLETED" else None,
            "error": "failed" if result_status == "FAILED" else None,
        },
    }
    if task_status.value == result_status:
        assert Task.model_validate(payload).result is not None
    else:
        with pytest.raises(ValidationError, match="status must match"):
            Task.model_validate(payload)


@pytest.mark.parametrize("status", [TaskStatus.COMPLETED, TaskStatus.FAILED])
def test_completed_or_failed_task_requires_result(status: TaskStatus) -> None:
    with pytest.raises(ValidationError, match="require an AgentResult"):
        Task(task_id="t1", session_id="s1", user_message="hello", status=status)


def test_task_result_must_belong_to_same_session() -> None:
    with pytest.raises(ValidationError, match="session_id must match"):
        Task(
            task_id="t1", session_id="s1", user_message="hello", status=TaskStatus.COMPLETED,
            result=AgentResult(session_id="s2", status="COMPLETED", response="answer"),
        )


@pytest.mark.parametrize(("model_type", "payload", "field"), [
    (Task, {"task_id": "t1", "session_id": "s1", "user_message": "hello"}, "task_id"),
    (Task, {"task_id": "t1", "session_id": "s1", "user_message": "hello"}, "session_id"),
    (Session, {"session_id": "s1"}, "session_id"),
    (AgentResult, {"session_id": "s1", "status": "CANCELLED"}, "session_id"),
    (ToolCall, {"id": "c1", "name": "test", "arguments": {}}, "id"),
    (Message, {"role": "tool", "content": "output", "tool_call_id": "c1"}, "tool_call_id"),
])
@pytest.mark.parametrize("invalid", ["", " \t\n", None])
def test_identifiers_cannot_be_empty(
    model_type: type[BaseModel], payload: dict[str, Any], field: str, invalid: str | None
) -> None:
    with pytest.raises(ValidationError):
        model_type.model_validate(payload | {field: invalid})


@pytest.mark.parametrize("role", ["system", "user", "tool"])
def test_only_assistant_can_request_tools(role: str) -> None:
    with pytest.raises(ValidationError, match="Only assistant"):
        Message.model_validate({
            "role": role, "content": "hello", "tool_call_id": "c1" if role == "tool" else None,
            "tool_calls": [{"id": "c1", "name": "test", "arguments": {}}],
        })


@pytest.mark.parametrize("payload", [
    {"role": "system"}, {"role": "user"}, {"role": "assistant"},
    {"role": "tool", "tool_call_id": "c1"},
])
def test_missing_message_content_requires_assistant_tool_calls(payload: dict[str, Any]) -> None:
    with pytest.raises(ValidationError, match="content is required"):
        Message.model_validate(payload)


def test_tool_error_can_be_represented_in_history() -> None:
    result = ToolResult(success=False, content="", error="provider unavailable")
    reply = Message(role="tool", content=result.model_dump_json(), tool_call_id="c1")
    restored = Message.model_validate_json(reply.model_dump_json())
    assert restored.tool_call_id == "c1"
    assert ToolResult.model_validate_json(restored.content) == result


@pytest.mark.parametrize(("success", "error"), [
    (True, "failed"), (True, ""), (False, None), (False, ""), (False, " \t\n"),
])
def test_tool_success_and_error_must_be_consistent(success: bool, error: str | None) -> None:
    with pytest.raises(ValidationError):
        ToolResult(success=success, content="", error=error)


@pytest.mark.parametrize(("model_type", "payload"), [
    (ToolDefinition, {"name": "test", "description": "test", "parameters": {}}),
    (ToolCall, {"id": "c1", "name": "test", "arguments": {}}),
    (Message, {"role": "user", "content": "hello"}),
    (ToolResult, {"success": True, "content": "ok"}),
    (TokenUsage, {}), (LLMResponse, {}), (CommandResult, {"exit_code": None}),
    (AgentResult, {"session_id": "s1", "status": "CANCELLED"}),
    (Task, {"task_id": "t1", "session_id": "s1", "user_message": "hello"}),
    (Session, {"session_id": "s1"}),
])
def test_exchange_models_reject_unknown_fields(
    model_type: type[BaseModel], payload: dict[str, Any]
) -> None:
    with pytest.raises(ValidationError, match="extra_forbidden"):
        model_type.model_validate(payload | {"unexpected": "must not disappear"})


def test_nested_exchange_models_reject_unknown_fields() -> None:
    with pytest.raises(ValidationError, match="extra_forbidden"):
        Task.model_validate({
            "task_id": "t1", "session_id": "s1", "user_message": "hello", "status": "CANCELLED",
            "result": {"session_id": "s1", "status": "CANCELLED", "unexpected": True},
        })


@pytest.mark.parametrize("payload", [{}, {"usage": None}])
def test_usage_may_be_unreported(payload: dict[str, Any]) -> None:
    response = LLMResponse.model_validate(payload)
    assert LLMResponse.model_validate_json(response.model_dump_json()).usage is None


@pytest.mark.parametrize("field", ["input_tokens", "output_tokens", "total_tokens"])
@pytest.mark.parametrize("invalid", [-1, 1.5, "1", True])
def test_token_counts_are_nonnegative_integers(field: str, invalid: Any) -> None:
    with pytest.raises(ValidationError):
        TokenUsage.model_validate({field: invalid})


def test_token_usage_does_not_compute_or_replace_unknown_counts() -> None:
    usage = TokenUsage(input_tokens=0, output_tokens=2)
    restored = TokenUsage.model_validate_json(usage.model_dump_json())
    assert restored.input_tokens == 0
    assert restored.output_tokens == 2
    assert restored.total_tokens is None
    assert TokenUsage().model_dump() == {
        "input_tokens": None, "output_tokens": None, "total_tokens": None,
    }


@pytest.mark.parametrize("reason", [None, "stop", "tool_calls", "length", "error", "unknown"])
def test_normalized_finish_reasons_round_trip(reason: str | None) -> None:
    response = LLMResponse.model_validate({"finish_reason": reason})
    assert LLMResponse.model_validate_json(response.model_dump_json()).finish_reason == reason


@pytest.mark.parametrize("reason", ["end_turn", "function_call", "", "STOP"])
def test_provider_finish_reason_requires_gateway_normalization(reason: str) -> None:
    with pytest.raises(ValidationError, match="finish_reason"):
        LLMResponse.model_validate({"finish_reason": reason})


@pytest.mark.parametrize("exit_code", [None, 0, -9, 124])
def test_timeout_preserves_known_or_unknown_exit_code(exit_code: int | None) -> None:
    result = CommandResult(exit_code=exit_code, stdout="partial output", stderr="timeout", timed_out=True)
    restored = CommandResult.model_validate_json(result.model_dump_json())
    assert restored.exit_code == exit_code
    assert restored.timed_out
    assert restored.stdout == "partial output"
