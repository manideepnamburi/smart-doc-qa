// Toggle.jsx
//
// Built as a real <button role="switch"> rather than a styled <div> with
// a click handler -- this is the accessible pattern: it's keyboard-
// focusable and reachable by Tab automatically (buttons are), and
// aria-checked tells screen readers its current state the same way a
// native checkbox would. A div with an onClick looks identical visually
// but is invisible to keyboard/screen-reader users entirely.

function Toggle({ label, checked, onChange }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      className={`toggle ${checked ? "toggle-on" : ""}`}
      onClick={() => onChange(!checked)}
    >
      <span className="toggle-track">
        <span className="toggle-thumb" />
      </span>
      <span className="toggle-label">{label}</span>
    </button>
  );
}

export default Toggle;
