"""Infrastructure errors derive from AgentError; no retry policy is implemented.

Expected tool failures use ToolResult(success=False, ...) instead of exceptions.
Concrete infrastructure exception subclasses belong to future implementations.
"""


class AgentError(Exception):
    """Base for explicit application errors, distinct from programming errors."""
