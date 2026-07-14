// Custom dropdown built on divs (no <select>). Native <select> crashes cohtml
// with a non-actionable runtime error. Native CS2 Dropdown from cs2/ui works
// but has its own padding/typography that didn't match our compact panel.
//
// Why the menu is portalled to document.body instead of rendered next to the
// toggle: the toggle lives inside Panel (overflow-y: auto) and LineRowOuter
// (overflow: hidden, for the accordion clip). An absolute-positioned menu
// inside either gets clipped — so we render the menu at the document root
// and compute its viewport-relative top/left from the toggle's bounding box.
//
// COHTML-safe checklist (see TTE's dropdown.tsx for the reference build):
//   - No transient props ($foo) — passes static styles + inline style for the
//     few values that depend on state. cohtml's styled-components integration
//     mis-resolves transient props occasionally.
//   - No transform on hover.
//   - click event (not mousedown) for the outside-click handler.
//   - Arrow as unicode glyph ▼/▲ — these two specifically render fine in
//     cohtml (unlike chevron arrows and many other symbols).

import { useState, useRef, useEffect, useLayoutEffect } from "react";
import { createPortal } from "react-dom";
import { styled } from "../styles/styled";
import { tokens as T } from "../styles/tokens";

export interface DropdownOption<V> {
  value: V;
  label: string;
}

export interface DropdownProps<V> {
  value: V;
  options: DropdownOption<V>[];
  onChange: (next: V) => void;
  placeholder?: string;
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

// Portal-mounted floater. Positioning is computed in JS from the toggle's
// bounding box so it sits flush under the toggle regardless of panel scroll
// or accordion overflow clipping.
// Solid (not glass) — the menu floats OVER the panel, and a translucent
// blurred surface there smears the covered controls into colour blotches.
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
  padding: ${T.space1} ${T.space2};
  color: ${T.colorTextPrimary};
  cursor: pointer;
  user-select: none;
  transition: background ${T.transitionFast};

  &:hover {
    background: ${T.colorRowBgHover};
  }
`;

const itemSelectedStyle = {
  background: T.colorAccentDim,
  color: T.colorTextPrimary,
};

export const Dropdown = <V,>({ value, options, onChange, placeholder = "—" }: DropdownProps<V>) => {
  const [isOpen, setIsOpen] = useState(false);
  const [menuRect, setMenuRect] = useState<{ top: number; left: number; width: number } | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const toggleRef = useRef<HTMLDivElement>(null);

  // Measure the toggle position whenever the menu opens. useLayoutEffect runs
  // before paint, so the menu appears at the right spot without a flicker.
  useLayoutEffect(() => {
    if (!isOpen || !toggleRef.current) return;
    const rect = toggleRef.current.getBoundingClientRect();
    setMenuRect({
      top: rect.bottom + 2,
      left: rect.left,
      width: rect.width,
    });
  }, [isOpen]);

  // Close on click outside. Uses `click` (not `mousedown`) — cohtml fires
  // mousedown unreliably for certain element types, click is consistent.
  useEffect(() => {
    if (!isOpen) return;
    const handler = (e: MouseEvent) => {
      const target = e.target as Node;
      // The menu is portalled, so it's not inside containerRef — check both.
      if (containerRef.current?.contains(target)) return;
      // Walk up looking for our menu (tag it via data attribute).
      let n: Node | null = target;
      while (n) {
        if ((n as HTMLElement).dataset?.trlDropdownMenu === "1") return;
        n = (n as HTMLElement).parentNode;
      }
      setIsOpen(false);
    };
    // Defer attach so the click that opened the menu doesn't immediately close it.
    const id = window.setTimeout(() => document.addEventListener("click", handler), 0);
    return () => {
      window.clearTimeout(id);
      document.removeEventListener("click", handler);
    };
  }, [isOpen]);

  const selected = options.find((o) => o.value === value);

  const handleSelect = (v: V) => {
    onChange(v);
    setIsOpen(false);
  };

  return (
    <Container ref={containerRef}>
      <Toggle ref={toggleRef} onClick={() => setIsOpen(!isOpen)}>
        <Label>{selected ? selected.label : placeholder}</Label>
        <Arrow>{isOpen ? "▲" : "▼"}</Arrow>
      </Toggle>
      {isOpen && menuRect &&
        createPortal(
          <Menu
            data-trl-dropdown-menu="1"
            style={{ top: menuRect.top, left: menuRect.left, width: menuRect.width }}
          >
            {options.map((opt, idx) => (
              <Item
                key={idx}
                style={opt.value === value ? itemSelectedStyle : undefined}
                onClick={() => handleSelect(opt.value)}
              >
                {opt.label}
              </Item>
            ))}
          </Menu>,
          document.body,
        )}
    </Container>
  );
};
