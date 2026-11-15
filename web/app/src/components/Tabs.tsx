import { useId, useRef, type KeyboardEvent, type ReactNode } from 'react';

export interface Tab {
  id: string;
  label: string;
  content: () => ReactNode;
}

/** Accessible tabs (WAI-ARIA tabs pattern): arrow keys, Home and End move between tabs; only the selected panel renders. */
export function Tabs({ tabs, selected, onSelect, label }: { tabs: readonly Tab[]; selected: string; onSelect: (id: string) => void; label: string }) {
  const base = useId();
  const refs = useRef(new Map<string, HTMLButtonElement>());
  const index = Math.max(0, tabs.findIndex((t) => t.id === selected));
  const current = tabs[index]!;

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const moves: Record<string, number> = { ArrowRight: index + 1, ArrowLeft: index - 1, Home: 0, End: tabs.length - 1 };
    const target = moves[event.key];
    if (target === undefined) {
      return;
    }

    event.preventDefault();
    const next = tabs[(target + tabs.length) % tabs.length]!;
    onSelect(next.id);
    refs.current.get(next.id)?.focus();
  };

  return (
    <div className="tabs">
      <div role="tablist" aria-label={label} onKeyDown={onKeyDown}>
        {tabs.map((tab) => (
          <button key={tab.id} type="button" role="tab" id={`${base}-${tab.id}`} aria-selected={tab.id === current.id} aria-controls={`${base}-${tab.id}-panel`}
            tabIndex={tab.id === current.id ? 0 : -1} ref={(element) => { if (element) { refs.current.set(tab.id, element); } }} onClick={() => onSelect(tab.id)}>
            {tab.label}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={`${base}-${current.id}-panel`} aria-labelledby={`${base}-${current.id}`} className="tab-panel">
        {current.content()}
      </div>
    </div>
  );
}
