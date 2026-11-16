"""Approved component exchange contracts; see docs/CONTRACTS.md.

Only payload structure is validated here. Core owns history consistency and
call ID uniqueness; Tool Registry owns registration and argument schemas.
"""

from enum import StrEnum
from typing import Annotated, Any, Literal, Self

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, model_validator


NonEmptyString = Annotated[str, StringConstraints(min_length=1, pattern=r"\S")]
AgentStatus = Literal["COMPLETED", "FAILED", "CANCELLED"]
FinishReason = Literal["stop", "tool_calls", "length", "error", "unknown"]


class _ExchangeModel(BaseModel):
    """Reject unrecognized contract fields instead of silently losing data."""

    model_config = ConfigDict(extra="forbid")


class ToolDefinition(_ExchangeModel):
    """Description passed to LLMClient; parameters is a JSON Schema object."""

    name: str
    description: str
    parameters: dict[str, Any]


class ToolCall(_ExchangeModel):
    """A provider-independent tool invocation; the caller supplies its ID."""

    id: NonEmptyString
    name: str
    arguments: dict[str, Any]


class Message(_ExchangeModel):
    """History entry; tool replies refer to an assistant ToolCall.id."""

    role: Literal["system", "user", "assistant", "tool"]
    content: str | None = None
    tool_calls: list[ToolCall] = Field(default_factory=list)
    tool_call_id: NonEmptyString | None = None

    @model_validator(mode="after")
    def validate_structure(self) -> Self:
        if self.role == "tool" and self.tool_call_id is None:
            raise ValueError("Tool messages must reference a tool_call_id")
        if self.tool_calls and self.role != "assistant":
            raise ValueError("Only assistant messages may contain tool_calls")
        if self.content is None and not (self.role == "assistant" and self.tool_calls):
            raise ValueError("Message content is required unless assistant calls tools")
        return self


class ToolResult(_ExchangeModel):
    """Tool execution payload; correlation belongs to Message.tool_call_id."""

    success: bool
    content: str
    error: NonEmptyString | None = None

    @model_validator(mode="after")
    def validate_outcome(self) -> Self:
        if self.success and self.error is not None:
            raise ValueError("Successful tool results must not contain an error")
        if not self.success and self.error is None:
            raise ValueError("Failed tool results require an error")
        return self


class TokenUsage(_ExchangeModel):
    """Optional reported counts; missing values are never computed or priced."""

    input_tokens: int | None = Field(default=None, ge=0, strict=True)
    output_tokens: int | None = Field(default=None, ge=0, strict=True)
    total_tokens: int | None = Field(default=None, ge=0, strict=True)


class LLMResponse(_ExchangeModel):
    """Gateway normalizes provider finish reasons to this shared vocabulary."""

    content: str | None = None
    tool_calls: list[ToolCall] = Field(default_factory=list)
    finish_reason: FinishReason | None = None
    usage: TokenUsage | None = None


class CommandResult(_ExchangeModel):
    """Sandbox output; exit_code=None when no process exit code is available."""

    exit_code: int | None
    stdout: str = ""
    stderr: str = ""
    timed_out: bool = False


class AgentResult(_ExchangeModel):
    """Terminal agent outcome; an iteration limit is FAILED with an explanation."""

    session_id: NonEmptyString
    response: str | None = None
    status: AgentStatus
    error: NonEmptyString | None = None

    @model_validator(mode="after")
    def validate_outcome(self) -> Self:
        if self.status == "COMPLETED":
            if self.response is None or not self.response.strip():
                raise ValueError("COMPLETED agent results require a nonblank response")
            if self.error is not None:
                raise ValueError("COMPLETED agent results must not contain an error")
        if self.status == "FAILED" and self.error is None:
            raise ValueError("FAILED agent results require an error")
        return self


class TaskStatus(StrEnum):
    """Minimum task states defined by ARCHITECTURE.md section 8."""

    PENDING = "PENDING"
    RUNNING = "RUNNING"
    COMPLETED = "COMPLETED"
    FAILED = "FAILED"
    CANCELLED = "CANCELLED"


class Task(_ExchangeModel):
    """Task envelope; pending/running tasks and early cancellation need no result."""

    task_id: NonEmptyString
    session_id: NonEmptyString
    user_message: str
    status: TaskStatus = TaskStatus.PENDING
    result: AgentResult | None = None

    @model_validator(mode="after")
    def validate_result(self) -> Self:
        if self.result is None:
            if self.status in (TaskStatus.COMPLETED, TaskStatus.FAILED):
                raise ValueError("COMPLETED and FAILED tasks require an AgentResult")
        else:
            if self.result.session_id != self.session_id:
                raise ValueError("Task and AgentResult session_id must match")
            if self.status != self.result.status:
                raise ValueError("Task and AgentResult status must match")
        return self


class Session(_ExchangeModel):
    """History identity created by the shared backend; no lifecycle is implemented."""

    session_id: NonEmptyString
