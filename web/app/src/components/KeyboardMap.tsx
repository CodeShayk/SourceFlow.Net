import * as Dialog from '@radix-ui/react-dialog';
import { SHORTCUTS } from '../keys.ts';
import { useStudio } from '../store.ts';

/** The keyboard map (`?`): every shortcut, in an accessible modal dialog. */
export function KeyboardMap() {
  const open = useStudio((s) => s.keyboardMapOpen);
  const setOpen = useStudio((s) => s.setKeyboardMapOpen);
  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content className="dialog" aria-describedby={undefined}>
          <Dialog.Title>Keyboard map</Dialog.Title>
          <table>
            <thead>
              <tr>
                <th scope="col">Keys</th>
                <th scope="col">Action</th>
              </tr>
            </thead>
            <tbody>
              {SHORTCUTS.map((shortcut) => (
                <tr key={shortcut.command}>
                  <td>
                    <kbd>{shortcut.keys}</kbd>
                  </td>
                  <td>{shortcut.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <Dialog.Close className="button">Close</Dialog.Close>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
