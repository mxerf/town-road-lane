// Dropdown built from divs. A native <select> crashes cohtml with an unhelpful runtime
// error, and the cs2/ui Dropdown brings padding and typography that don't fit this compact
// panel.
//
// The menu is portalled to document.body: the toggle sits inside Panel (overflow-y: auto)
// and LineRowOuter (overflow: hidden for the accordion), and either would clip an absolutely
// positioned menu. Its viewport position comes from the toggle's bounding box.
//
// Modelled on TrafficToolEssentials' dropdown.tsx. cohtml notes:
//   - No transient props ($foo): cohtml's styled-components integration sometimes
//     resolves them wrong, so state-dependent values go through the inline style.
//   - No transform on hover.
//   - The outside-click handler listens to click, not mousedown.
//   - ▼ and ▲ render fine in cohtml, unlike chevrons and many other symbols.

import { ReactNode, useState, useRef, useEffect, useLayoutEffect } from "react";
import { createPortal } from "react-dom";
import { styled } from "../styles/styled";
import { tokens as T } from "../styles/tokens";

export interface DropdownOption<V> {
  value: V;
  label: string;
  /** Optional visual sample rendered before the label (style swatches etc). */
  preview?: ReactNode;
  /** Pinned favourite, shown with a filled pin; callers sort pinned options first. */
  pinned?: boolean;
}

export interface DropdownProps<V> {
  value: V;
  options: DropdownOption<V>[];
  onChange: (next: V) => void;
  placeholder?: string;
  /** Fires on menu open and close. Popover hosts need it: the menu is portalled to
   *  document.body, so moving the cursor into it leaves the host element, and a
   *  hover-expanded popover would collapse and unmount the open dropdown. */
  onOpenChange?: (open: boolean) => void;
  /** When set, every menu item gets a pin button that toggles the option's favourite
   *  status. Clicking the pin neither selects the option nor closes the menu; the list
   *  re-sorts on the next binding push. */
  onTogglePin?: (v: V) => void;
}

const Container = styled.div`
  position: relative;
  width: 100%;
  font-size: ${T.fontSizeMd};
`;

const Toggle = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: ${T.space1} ${T.space2};
  background: rgba(8, 12, 18, 0.75);
  color: ${T.colorTextPrimary};
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusSm};
  cursor: pointer;
  user-select: none;
  transition: background ${T.transitionFast}, border-color ${T.transitionFast};

  &:hover {
    background: rgba(8, 12, 18, 0.95);
    border-color: ${T.colorBorderStrong};
  }
`;

const Label = styled.span`
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
`;

const Arrow = styled.span`
  font-size: 9rem;
  color: ${T.colorTextMuted};
  margin-left: ${T.space2};
`;

// Solid rather than glass: the menu floats over the panel, and a blurred translucent
// surface smears the controls it covers.
const Menu = styled.div`
  position: fixed;
  background: ${T.colorSurfaceSolid};
  border: 1rem solid ${T.colorBorderMid};
  border-radius: ${T.radiusSm};
  box-shadow: ${T.shadowMd};
  z-index: 999999;
  max-height: 240rem;
  overflow-y: auto;
  font-size: ${T.fontSizeMd};
`;

const Item = styled.div`
  display: flex;
  align-items: center;
  padding: ${T.space1} ${T.space2};
  color: ${T.colorTextPrimary};
  cursor: pointer;
  user-select: none;
  transition: background ${T.transitionFast};

  &:hover {
    background: ${T.colorRowBgHover};
  }
`;

// nowrap sets the menu's shrink-wrap width. The ellipsis only shows when the screen-edge
// maxWidth limits the menu, and the pin is never cut off.
const ItemLabel = styled.span`
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
`;

// Swatch before the label. flex-shrink 0 so a long label never squeezes it.
const Preview = styled.span`
  display: flex;
  align-items: center;
  flex-shrink: 0;
  margin-right: ${T.space2};
`;

// Padded well beyond the 12px glyph: rows are dense, and a near miss selects the option.
const PinBtn = styled.span`
  display: flex;
  align-items: center;
  flex-shrink: 0;
  margin-left: auto;
  padding: 2rem 2rem 2rem ${T.space2};
  border-radius: ${T.radiusSm};

  &:hover {
    background: rgba(255, 255, 255, 0.12);
  }
`;

// SVG star, since unicode glyph coverage in cohtml is unreliable.
const PinIcon = ({ active }: { active: boolean }) => (
  <svg width={12} height={12} viewBox="0 0 12 12" fill="none">
    <path
      d="M6 1 L7.4 4.2 L10.9 4.5 L8.2 6.8 L9 10.2 L6 8.4 L3 10.2 L3.8 6.8 L1.1 4.5 L4.6 4.2 Z"
      fill={active ? "#f2c94c" : "none"}
      stroke={active ? "#f2c94c" : "rgba(255, 255, 255, 0.4)"}
      strokeWidth={1}
    />
  </svg>
);

const itemSelectedStyle = {
  background: T.colorAccentDim,
  color: T.colorTextPrimary,
};

export const Dropdown = <V,>({ value, options, onChange, placeholder = "—", onOpenChange, onTogglePin }: DropdownProps<V>) => {
  const [isOpen, setIsOpen] = useState(false);
  const setOpen = (next: boolean) => {
    setIsOpen(next);
    onOpenChange?.(next);
  };
  const [menuRect, setMenuRect] = useState<{ top: number; left: number; width: number; maxWidth: number } | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const toggleRef = useRef<HTMLDivElement>(null);

  // useLayoutEffect runs before paint, so the menu opens in place without a flicker.
  useLayoutEffect(() => {
    if (!isOpen || !toggleRef.current) return;
    const rect = toggleRef.current.getBoundingClientRect();
    setMenuRect({
      top: rect.bottom + 2,
      left: rect.left,
      width: rect.width,
      // The menu may be wider than the toggle, but not past the right screen edge
      // (popovers can sit close to it).
      maxWidth: Math.max(rect.width, window.innerWidth - rect.left - 8),
    });
  }, [isOpen]);

  // Close on outside click. cohtml fires mousedown unreliably on some elements; click is
  // consistent.
  useEffect(() => {
    if (!isOpen) return;
    const handler = (e: MouseEvent) => {
      const target = e.target as Node;
      // The menu is portalled, so it isn't inside containerRef; look for its data attribute.
      if (containerRef.current?.contains(target)) return;
      let n: Node | null = target;
      while (n) {
        if ((n as HTMLElement).dataset?.trlDropdownMenu === "1") return;
        n = (n as HTMLElement).parentNode;
      }
      setOpen(false);
    };
    // Attach later so the click that opened the menu doesn't close it.
    const id = window.setTimeout(() => document.addEventListener("click", handler), 0);
    return () => {
      window.clearTimeout(id);
      document.removeEventListener("click", handler);
    };
  }, [isOpen]);

  const selected = options.find((o) => o.value === value);

  const handleSelect = (v: V) => {
    onChange(v);
    setOpen(false);
  };

  return (
    <Container ref={containerRef}>
      <Toggle ref={toggleRef} onClick={() => setOpen(!isOpen)}>
        {selected?.preview && <Preview>{selected.preview}</Preview>}
        <Label>{selected ? selected.label : placeholder}</Label>
        <Arrow>{isOpen ? "▲" : "▼"}</Arrow>
      </Toggle>
      {isOpen && menuRect &&
        createPortal(
          <Menu
            data-trl-dropdown-menu="1"
            // At least as wide as the toggle; otherwise the fixed-position menu shrink-wraps
            // to its widest item, so long labels and the pin button fit.
            style={{ top: menuRect.top, left: menuRect.left, minWidth: menuRect.width, maxWidth: menuRect.maxWidth }}
          >
            {options.map((opt, idx) => (
              <Item
                key={idx}
                style={opt.value === value ? itemSelectedStyle : undefined}
                onClick={() => handleSelect(opt.value)}
              >
                {opt.preview && <Preview>{opt.preview}</Preview>}
                <ItemLabel>{opt.label}</ItemLabel>
                {onTogglePin && (
                  <PinBtn
                    onClick={(e: React.MouseEvent) => {
                      e.stopPropagation();
                      onTogglePin(opt.value);
                    }}
                  >
                    <PinIcon active={!!opt.pinned} />
                  </PinBtn>
                )}
              </Item>
            ))}
          </Menu>,
          document.body,
        )}
    </Container>
  );
};
