// Tooltips in place of the native `title` attribute, which cohtml renders badly or not at
// all. Adapted from TrafficToolEssentials' tooltip-context.tsx.
//
// TooltipProvider holds the one active tooltip, <Tooltip> wraps a target and calls
// show/hide on mouse enter/leave, and a single <div> portalled to document.body renders the
// content outside the panel's overflow and transforms.
//
// cohtml notes: the tooltip needs `display: block` (the default is flex, which wraps lines
// badly); measurement after render uses setTimeout(0) rather than requestAnimationFrame; no
// transform, and pointer-events: none so the tooltip never takes clicks.

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

// Returns no-ops without a provider, so a <Tooltip> outside one doesn't crash.
const useTooltip = (): TooltipContextValue => {
  const ctx = useContext(TooltipContext);
  return ctx ?? { show: () => {}, hide: () => {} };
};

const tooltipBaseStyle: CSSProperties = {
  position: "fixed",
  display: "block",
  fontSize: "11rem",
  color: T.colorTextPrimary,
  // Solid, no blur: tooltips often sit over the panel itself, where a glass surface smears
  // the controls underneath.
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
    // Render invisibly first to measure the box, then position it.
    setState({ content, left: 0, top: 0, visible: false });

    setTimeout(() => {
      if (!measureRef.current) return;
      const ttRect = measureRef.current.getBoundingClientRect();
      const screenW = window.innerWidth;
      const screenH = window.innerHeight;

      // Flip to the other side if the requested one doesn't fit.
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

// Wraps children in a span that catches the mouse events. The span uses display: flex
// because cohtml fails to parse inline-flex (Player.log shows the error) and leaves the
// default display; every mount point is a flex container anyway.
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
