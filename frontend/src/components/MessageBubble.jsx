// MessageBubble.jsx
//
// Renders a single message. For assistant messages, also surfaces WHERE
// the answer came from (grounded in documents / general knowledge
// fallback / not found) as a visible badge -- this is literally the
// AnswerSource enum from the backend's QAResult, made visible instead of
// staying a hidden API field.

// answerSource arrives from the API as a raw integer, matching the C#
// enum's declaration order: Document = 0, LLMFallback = 1, NotFound = 2.
const ANSWER_SOURCE_LABELS = {
  0: { label: "Grounded in documents", className: "source-document" },
  1: { label: "From general knowledge", className: "source-fallback" },
  2: { label: "Not found in documents", className: "source-notfound" },
};

// showCitations is passed down from App.jsx (driven by the toggle) --
// citations always ARRIVE from the API regardless of this setting, this
// prop only controls whether they're actually rendered.
function MessageBubble({ message, showCitations }) {
  const isUser = message.role === "user";
  const sourceInfo = !isUser && message.answerSource !== undefined
    ? ANSWER_SOURCE_LABELS[message.answerSource]
    : null;

  return (
    <div className={`message ${isUser ? "message-user" : "message-assistant"}`}>
      <div className="message-role">{isUser ? "You" : "SmartDocQA"}</div>

      {sourceInfo && (
        <div className={`answer-source-badge ${sourceInfo.className}`}>
          {sourceInfo.label}
        </div>
      )}

      <div className="message-content">{message.content}</div>

      {showCitations && message.citations && message.citations.length > 0 && (
        <div className="message-citations">
          {message.citations.map((citation) => (
            <span className="citation-chip" key={citation.chunkId}>
              {citation.fileName.replace(/\.pdf$/i, "")} · p.{citation.pageNumber}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

export default MessageBubble;
