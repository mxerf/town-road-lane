// Custom tooltip system, cohtml-safe. Replaces native `title` attributes
// which cohtml either renders ugly or not at all.
//
// Architecture (adapted from TTE's tooltip-context.tsx):
//   - TooltipProvider creates a singleton "active tooltip" state via context.
//   - <Tooltip> wraps a target element; on mouseenter it calls show(content,
//     rect, position), on mouseleave it calls hide().
//   - A single floating <div> renders the active tooltip's content, portalled
//     to document.body so it escapes panel overflow / transforms.
//
// cohtml-safe checklist:
//   - `display: block` (NOT flex — cohtml default is flex which line-wraps).
//   - `position: fixed` works because we portal to body.
//   - setTimeout(0) for post-render measurement (no requestAnimationFrame).
//   - No transform; pointer-events: none so tooltip doesn't intercept clicks.

import {
  createContext,
  CSSProperties,
  ReactNode,
  useCallback,
  useContext,
  useRef,
  useState,
} from "react";
import { createPortal } from "react-dom";
import { tokens as T } from "../styles/tokens";

const OFFSET = 6;
const EDGE_PADDING = 8;

export type TooltipPosition = "bottom" | "top";

interface TooltipState {
  content: ReactNode;
  left: number;
  top: number;
  visible: boolean;
}

interface TooltipContextValue {
  show: (content: ReactNode, rect: DOMRect, position: TooltipPosition) => void;
  hide: () => void;
}

const TooltipContext = createContext<TooltipContextValue | null>(null);

// Hook for the wrapper component. Tolerant of provider absence (returns no-op)
// so a stray <Tooltip> outside the provider doesn't crash — useful during the
// rollout when not every mount point has the provider yet.
const useTooltip = (): TooltipContextValue => {
  const ctx = useContext(TooltipContext);
  return ctx ?? { show: () => {}, hide: () => {} };
};

const tooltipBaseStyle: CSSProperties = {
  position: "fixed",
  display: "block",
  fontSize: "11rem",
  color: T.colorTextPrimary,
  // Solid, no blur — tooltips frequently hover over the panel itself, where a
  // glass surface smears the underlying controls (same reasoning as Dropdown).
  background: T.colorSurfaceSolid,
  border: `1rem solid ${T.colorBorderMid}`,
  borderRadius: T.radiusSm,
  padding: "5rem 8rem",
  margin: 0,
  textAlign: "left",
  zIndex: 9999999,
  maxWidth: "220rem",
  whiteSpace: "normal",
  wordWrap: "break-word",
  lineHeight: 1.35,
  pointerEvents: "none",
  boxShadow: T.shadowSm,
};

export const TooltipProvider = ({ children }: { children: ReactNode }) => {
  const [state, setState] = useState<TooltipState>({
    content: null,
    left: 0,
    top: 0,
    visible: false,
  });
  const measureRef = useRef<HTMLDivElement>(null);

  const show = useCallback((content: ReactNode, rect: DOMRect, position: TooltipPosition) => {
    // Render invisibly first so we can measure the tooltip box, then position.
    setState({ content, left: 0, top: 0, visible: false });

    setTimeout(() => {
      if (!measureRef.current) return;
      const ttRect = measureRef.current.getBoundingClientRect();
      const screenW = window.innerWidth;
      const screenH = window.innerHeight;

      // Auto-flip if requested position doesn't fit.
      let pos = position;
      const spaceBelow = screenH - rect.bottom - EDGE_PADDING;
      const spaceAbove = rect.top - EDGE_PADDING;
      if (pos === "bottom" && spaceBelow < ttRect.height && spaceAbove > spaceBelow) {
        pos = "top";
      } else if (pos === "top" && spaceAbove < ttRect.height && spaceBelow > spaceAbove) {
        pos = "bottom";
      }

      let left = rect.left + rect.width / 2 - ttRect.width / 2;
      const top =
        pos === "bottom" ? rect.bottom + OFFSET : rect.top - ttRect.height - OFFSET;

      // Clamp horizontally to viewport.
      left = Math.max(
        EDGE_PADDING,
        Math.min(left, screenW - ttRect.width - EDGE_PADDING),
      );

      setState({ content, left, top: Math.max(EDGE_PADDING, top), visible: true });
    }, 0);
  }, []);

  const hide = useCallback(() => {
    setState((prev) => ({ ...prev, visible: false, content: null }));
  }, []);

  return (
    <TooltipContext.Provider value={{ show, hide }}>
      {children}
      {state.content &&
        createPortal(
          <div
            ref={measureRef}
            style={{
              ...tooltipBaseStyle,
              left: state.left,
              top: state.top,
              opacity: state.visible ? 1 : 0,
            }}
          >
            {state.content}
          </div>,
          document.body,
        )}
    </TooltipContext.Provider>
  );
};

// Wrapper for any element that should show a tooltip. Renders children inside
// a span that captures mouse events. display: flex (NOT inline-flex — cohtml
// fails to parse it, see Player.log, leaving the span at its default display)
// so the wrapper hugs its child deterministically; every mount point is a
// flex container anyway (cohtml's default display is flex).
export const Tooltip = ({
  content,
  position = "bottom",
  children,
}: {
  content: ReactNode;
  position?: TooltipPosition;
  children: ReactNode;
}) => {
  const { show, hide } = useTooltip();
  const ref = useRef<HTMLSpanElement>(null);

  return (
    <span
      ref={ref}
      style={{ display: "flex" }}
      onMouseEnter={() => {
        if (ref.current) show(content, ref.current.getBoundingClientRect(), position);
      }}
      onMouseLeave={hide}
    >
      {children}
    </span>
  );
};
