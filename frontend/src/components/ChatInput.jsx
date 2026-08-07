// ChatInput.jsx
//
// This component DOES have its own useState -- for the text currently
// being typed. That's genuinely local, temporary state that only this
// component cares about (nobody else needs to know what's half-typed in
// the box), which is exactly when a component-local useState is the right
// call, versus lifting state up to a shared hook like useConversation.

import { useState } from "react";

function ChatInput({ onSend, isLoading }) {
  const [text, setText] = useState("");

  function handleSubmit(e) {
    e.preventDefault(); // stop the browser's default "reload the page" form behavior
    onSend(text);
    setText(""); // clear the box after sending
  }

  return (
    <form className="chat-input" onSubmit={handleSubmit}>
      <input
        type="text"
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder="Ask a question about the ingested documents..."
        disabled={isLoading}
      />
      <button type="submit" disabled={isLoading || !text.trim()}>
        {isLoading ? "Thinking..." : "Send"}
      </button>
    </form>
  );
}

export default ChatInput;
