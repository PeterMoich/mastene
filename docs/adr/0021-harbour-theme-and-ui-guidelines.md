# ADR-0021: The Harbour theme and the UI guidelines

Status: Accepted (5 November 2026)

## Context

The app is now called Mastene in the interface. The default Angular Material azure look had a light blue top
bar and no brand. Karabo asked for the UI to follow three guides together: Nielsen's usability heuristics,
Material Design 3 and Apple's Human Interface Guidelines, with a navy top bar. Four themes were previewed
(Harbour, Estate, Veld, Graphite) and Harbour was chosen.

## Decision

- **Harbour**: a navy bar (#13294B) with a teal mark, primary #1F4E8C, teal tertiary #00787A. `mat.theme`
  builds the full Material 3 palette from the built-in azure and cyan palettes, then `styles.scss` sets the
  key colour roles to Harbour's exact values. Every role is a `light-dark()` pair.
- **Light and dark** follow the device by default (HIG). The account menu offers Same as device, Light or
  Dark; the choice is a class on `<html>` that sets `color-scheme`, kept in this browser's local storage.
- **Contrast**: text and controls are at least 4.5:1 (WCAG AA) in both modes.
- **Fonts**: the device's system font (SF on Apple, Segoe on Windows, Roboto on Android) instead of a
  downloaded Roboto. Icons stay Material Symbols.
- **Status**: one `.app-chip` style everywhere (leases, issues, payments, escalation) with words, an icon and
  a tone (success, warning, danger, info, neutral, muted). Colour is never the only signal.
- **Brand**: the Mastene mark (a roof over an M) and name in the top bar, on the sign-in page and as the
  favicon. Page titles end in "· Mastene".
- **Heuristics applied**: the account menu groups who you are, appearance and sign out; the icon-only rail on
  small screens has tooltips; the password can be shown; sign-in errors sit in a clear block with an icon.

## Consequences

- Changing a colour is a one-line change in `styles.scss` (both modes side by side).
- The built-in palettes still supply the tones Harbour doesn't set (for example, hover and disabled states),
  so a few rarely seen states use azure rather than Harbour's exact navy.
- The product, repository and API keep their technical names (TenantManager.*); only what people see says
  Mastene.

## Update (5 November 2026): first review

- **Tenant home** replaces "My details": a greeting with the three most common actions (report an issue,
  upload proof of payment, message the landlord), then rent, lease term and repairs at a glance, then their
  details and WhatsApp consent.
- **Tables**: one bordered card with the paginator as its footer, a tinted header with small capitals, row
  hover, linked names in the primary colour, and an icon on empty states. Content is centred up to 1280 px.
- **Messages**: a chat card with a header, bubbles, an empty state and the composer as its footer.
- **Escalated** is a small badge on the issue's detail line instead of a full-size chip beside the title.
- **Sign-in**: a photo panel ("Apartment building with balconies" by Cristian Pineda, Unsplash License,
  stored in `web/public/images`, so no request goes to Unsplash) and no browser autofill tint.
