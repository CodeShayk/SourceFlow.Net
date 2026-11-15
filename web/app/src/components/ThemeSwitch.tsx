import { useStudio } from '../store.ts';
import type { ThemePreference } from '../theme.ts';

const CHOICES: Array<{ value: ThemePreference; label: string }> = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

/** Light, dark or the system's theme (a labelled radio group). */
export function ThemeSwitch() {
  const theme = useStudio((s) => s.theme);
  const setTheme = useStudio((s) => s.setTheme);
  return (
    <fieldset className="theme-switch">
      <legend>Theme</legend>
      {CHOICES.map((choice) => (
        <label key={choice.value}>
          <input type="radio" name="theme" value={choice.value} checked={theme === choice.value} onChange={() => setTheme(choice.value)} />
          {choice.label}
        </label>
      ))}
    </fieldset>
  );
}
