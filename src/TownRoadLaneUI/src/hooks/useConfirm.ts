import { useCallback, useEffect, useMemo, useState } from "react";

// Two-press confirm for destructive actions: the first press arms, a second press within
// CONFIRM_TIMEOUT_MS runs the action, and an arm left alone expires on its own.
export const CONFIRM_TIMEOUT_MS = 3000;

export interface KeyedConfirm<K> {
  // Key armed by the first press, null while disarmed.
  armed: K | null;
  arm: (key: K) => void;
  disarm: () => void;
  // Arms `key`, or disarms and runs `action` when `key` is already armed.
  press: (key: K, action: () => void) => void;
}

// The armed state holds a key so one hook can guard a whole list: the Delete key arms a
// particular line, and pressing it on another line re-arms rather than deletes. Arming a
// different key restarts the timeout.
export const useKeyedConfirm = <K>(): KeyedConfirm<K> => {
  const [armed, setArmed] = useState<K | null>(null);

  useEffect(() => {
    if (armed === null) return;
    const id = window.setTimeout(() => setArmed(null), CONFIRM_TIMEOUT_MS);
    return () => window.clearTimeout(id);
  }, [armed]);

  const arm = useCallback((key: K) => setArmed(key), []);
  const disarm = useCallback(() => setArmed(null), []);
  const press = useCallback(
    (key: K, action: () => void) => {
      if (armed === key) {
        setArmed(null);
        action();
      } else {
        setArmed(key);
      }
    },
    [armed],
  );

  return useMemo(() => ({ armed, arm, disarm, press }), [armed, arm, disarm, press]);
};

export interface Confirm {
  armed: boolean;
  arm: () => void;
  disarm: () => void;
  // One button pressed twice.
  press: (action: () => void) => void;
  // The confirm button of a separate confirm row: runs `action` whatever the armed state.
  confirm: (action: () => void) => void;
}

// A single button or confirm row.
export const useConfirm = (): Confirm => {
  const { armed, arm, disarm, press } = useKeyedConfirm<true>();
  return useMemo(
    () => ({
      armed: armed !== null,
      arm: () => arm(true),
      disarm,
      press: (action: () => void) => press(true, action),
      confirm: (action: () => void) => {
        disarm();
        action();
      },
    }),
    [armed, arm, disarm, press],
  );
};
