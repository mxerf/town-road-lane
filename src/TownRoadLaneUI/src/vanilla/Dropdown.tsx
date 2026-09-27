// The game's Dropdown / DropdownItem / DropdownToggle behind a plain value/options/onChange
// contract, with the vanilla theme, focus and sounds already wired.

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
  // Applied to a wrapper <div>: the cs2/ui Dropdown does not accept className.
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
