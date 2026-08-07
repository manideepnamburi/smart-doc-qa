// App.jsx
//
// This component's job is thin on purpose: get `messages`/`sendMessage`
// from the useConversation hook, and render them. Almost none of the
// actual LOGIC lives here -- that's deliberate. Keeping components mostly
// about "what to render" and pushing the "how it works" into hooks is a
// pattern you'll see in almost every real React codebase.

import { useState } from "react";
import { useConversation } from "./hooks/useConversation";
import { useBranding } from "./hooks/useBranding";
import { useAppConfig } from "./hooks/useAppConfig";
import MessageBubble from "./components/MessageBubble";
import ChatInput from "./components/ChatInput";
import Toggle from "./components/Toggle";
import "./App.css";

function App() {
  const { messages, sendMessage, isLoading, clearConversation } = useConversation();
  const branding = useBranding();
  const config = useAppConfig();

  // These start as plain `false` (matching the required defaults) so
  // behavior is correct even in the brief window before config.json has
  // finished loading. Once useAppConfig's fetch resolves, App re-renders
  // with the real config -- but these toggle values themselves are
  // independent local state, since the USER can flip them at any point
  // after that, regardless of what the config default was.
  const [showCitations, setShowCitations] = useState(config.defaultShowCitations);
  const [useFallback, setUseFallback] = useState(config.defaultUseFallback);

  function handleSend(text) {
    sendMessage(text, { fallbackToLLM: useFallback });
  }

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-brand">
          <img src={branding.logoUrl} alt="" className="app-logo" />
          <div>
            <h1>{branding.appName}</h1>
            <div className="app-subtitle">{branding.tagline}</div>
          </div>
        </div>

        <div className="app-controls">
          {config.showCitationsToggle && (
            <Toggle
              label="Show citations"
              checked={showCitations}
              onChange={setShowCitations}
            />
          )}
          {config.showFallbackToggle && (
            <Toggle
              label="Use general knowledge"
              checked={useFallback}
              onChange={setUseFallback}
            />
          )}
          <button className="clear-button" onClick={clearConversation}>
            New Conversation
          </button>
        </div>
      </header>

      <div className="message-list">
        {messages.length === 0 && (
          <div className="empty-state">
            Ask about the ingested documents. Every answer traces back to an exact page — or tells you plainly it couldn't find one.
          </div>
        )}

        {messages.map((message, index) => (
          <MessageBubble key={index} message={message} showCitations={showCitations} />
        ))}

        {isLoading && (
          <div className="message message-assistant message-loading">
            <div className="message-role">SmartDocQA</div>
            <div className="message-content">Thinking...</div>
          </div>
        )}
      </div>

      <ChatInput onSend={handleSend} isLoading={isLoading} />
    </div>
  );
}

export default App;
