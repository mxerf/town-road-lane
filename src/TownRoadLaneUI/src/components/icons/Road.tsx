import { IconBase, IconProps } from "./Icon";

// Toolbar icon: stylized intersection / road with dashed centerline. Used on
// the GameTopLeft toggle button to replace the textual "TRL" label.
export const Road = (props: IconProps) => (
  <IconBase {...props}>
    <path d="M3 2 L 5 14" />
    <path d="M13 2 L 11 14" />
    <line x1="8" y1="3"  x2="8" y2="5"  />
    <line x1="8" y1="7"  x2="8" y2="9"  />
    <line x1="8" y1="11" x2="8" y2="13" />
  </IconBase>
);
