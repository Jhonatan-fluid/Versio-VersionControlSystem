/**
 * The Versio mark, drawn in the page rather than only in the favicon: two plates of equal
 * weight — the text mirror on top, the binary project below — and the agreement disc notching
 * both. The header carried GitHub's octocat until 2026-10-24, when the owner's first look at
 * the GitHub-shaped page said the shape is right and the brand is wrong: the page looks like
 * github.com, it is not github.com, and the one thing that says so is the mark.
 *
 * FOUR PLACES CARRY THIS GEOMETRY, and the header of assets/versio.svg says so: that reference
 * drawing, tools/New-VersioIcon.ps1 which redraws it per icon size, Brand.xaml which is the
 * desktop's top bar at 20 px, and this. Change one, change all four. `VersioMark.test.tsx`
 * pins this one to the SVG's numbers so a drift here is a red test, not a slightly wrong logo.
 *
 * The same two departures from the SVG that Brand.xaml makes, for the same reasons:
 *
 * - NO TILE. The rounded square is what makes an icon readable against an unknown tab strip;
 *   inside a header that is already the page's surface it would be a square with an edge.
 * - The notch is punched in the header's own colour, so it reads as a hole cut out of both
 *   plates rather than a disc dropped on the gap. That is why it takes the surface's token
 *   rather than the tile's #1D2026: on the light theme the header is white.
 *
 * Colours are the page's tokens (styles/tokens.css, the desktop palette), so the mark follows
 * the theme switch like everything else: plates in the text colours, the disc in the "added"
 * green that means "the two sides agree".
 */
export function VersioMark({ size = 32, surface = 'var(--color-header-bg)' }: { size?: number; surface?: string }) {
  return (
    <svg viewBox="0 0 64 64" width={size} height={size} role="img" aria-label="Versio" className="versio-mark">
      <title>Versio</title>
      <rect x="13" y="15" width="38" height="14" rx="4" fill="var(--color-fg-default)" />
      <rect x="13" y="35" width="38" height="14" rx="4" fill="var(--color-fg-subtle)" />
      <circle cx="32" cy="32" r="8.5" fill={surface} />
      <circle cx="32" cy="32" r="6" fill="var(--color-success-fg)" />
    </svg>
  );
}
