// Typed wrapper around CS2's native Dropdown. Generic over the option value
// type so call sites get TS inference on the selection callback.
//
// Usage:
//   <VanillaDropdown
//     value={currentStyle}
//     options={STYLE_OPTIONS}      // [{ value: 0, label: "Solid" }, ...]
//     onChange={(v) => setStyle(v)}
//   />
//
// Why this exists: cs2/ui exposes Dropdown / DropdownItem / DropdownToggle
// as primitives with verbose theming + focus + sounds plumbing. Most of our
// dropdowns want the same behaviour, so we hide the boilerplate behind a
// single component with a simple value/options/onChange contract.

import { Dropdown, DropdownItem, DropdownToggle, FOCUS_AUTO } from "cs2/ui";
import { vanillaDropdownTheme } from "./theme";

export interface VanillaDropdownOption<T> {
  value: T;
  label: string;
}

export interface VanillaDropdownProps<T> {
  value: T;
  options: VanillaDropdownOption<T>[];
  onChange: (next: T) => void;
  // `className` is applied to the wrapper <div> rather than the cs2/ui
  // Dropdown — the Dropdown component itself doesn't accept className.
  className?: string;
}

export const VanillaDropdown = <T,>({
  value,
  options,
  onChange,
  className,
}: VanillaDropdownProps<T>) => {
  const selected = options.find((o) => o.value === value);

  const items = options.map((opt, idx) => (
    <DropdownItem<number>
      key={idx}
      theme={vanillaDropdownTheme}
      focusKey={FOCUS_AUTO}
      value={idx}
      closeOnSelect={true}
      // `selected` controls the visual checkmark on the open menu — true on the
      // currently-active option so the user can see what's set without reading.
      selected={opt.value === value}
      onToggleSelected={() => onChange(opt.value)}
      sounds={{ select: "select-item" }}
    >
      {opt.label}
    </DropdownItem>
  ));

  return (
    <div className={className}>
      <Dropdown
        focusKey={FOCUS_AUTO}
        theme={vanillaDropdownTheme}
        content={items}
      >
        <DropdownToggle>
          <span>{selected ? selected.label : "—"}</span>
        </DropdownToggle>
      </Dropdown>
    </div>
  );
};
