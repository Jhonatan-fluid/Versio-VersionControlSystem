import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Link } from 'react-router';
import { Icon, type IconName } from './Icon';

/**
 * The page's base components, in GitHub's grammar (Primer's names): buttons, counters, labels,
 * state badges, avatars, the underline nav, blank slates, flashes, a spinner and relative times.
 * Each is a thin, styled element — nothing here fetches, and nothing here draws code. The styles
 * are in app.css under the same names.
 */

type Variant = 'default' | 'primary' | 'danger' | 'invisible';
type Size = 'small' | 'medium' | 'large';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: Variant;
  size?: Size;
  leadingIcon?: IconName;
  trailingIcon?: IconName;
  /** A count drawn after the text, as GitHub's "Files changed 85". */
  count?: number;
}

export function Button({ variant = 'default', size = 'medium', leadingIcon, trailingIcon, count, className, children, type = 'button', ...rest }: ButtonProps) {
  return (
    <button type={type} className={`btn btn-${variant} btn-${size}${className ? ` ${className}` : ''}`} {...rest}>
      {leadingIcon && <Icon name={leadingIcon} className="btn-icon" />}
      {children && <span className="btn-text">{children}</span>}
      {count !== undefined && <Counter>{count}</Counter>}
      {trailingIcon && <Icon name={trailingIcon} className="btn-icon btn-icon-trailing" />}
    </button>
  );
}

/** A link drawn as a button (a route, or a plain href for sign-in and downloads). */
export function LinkButton({ to, href, variant = 'default', size = 'medium', leadingIcon, count, className, children, title }: {
  to?: string; href?: string; variant?: Variant; size?: Size; leadingIcon?: IconName; count?: number; className?: string; children: ReactNode; title?: string;
}) {
  const cls = `btn btn-${variant} btn-${size}${className ? ` ${className}` : ''}`;
  const body = (
    <>
      {leadingIcon && <Icon name={leadingIcon} className="btn-icon" />}
      <span className="btn-text">{children}</span>
      {count !== undefined && <Counter>{count}</Counter>}
    </>
  );
  return to ? <Link to={to} className={cls} title={title}>{body}</Link> : <a href={href} className={cls} title={title}>{body}</a>;
}

/** A small filled count, GitHub's Counter. */
export function Counter({ children, scheme = 'default' }: { children: ReactNode; scheme?: 'default' | 'primary' }) {
  return <span className={`counter counter-${scheme}`}>{children}</span>;
}

/**
 * An issue label, filled with its colour and lettered for contrast the way GitHub does it:
 * a light fill takes dark text, a dark fill takes light. The colour is six hex digits the
 * server checked (security rule 9), or empty for a label with no colour.
 */
export function IssueLabel({ name, color, children, title }: { name: string; color?: string | null; children?: ReactNode; title?: string }) {
  const style = color && /^[0-9a-fA-F]{6}$/.test(color) ? { backgroundColor: `#${color}`, color: contrastOn(color), borderColor: 'transparent' } : undefined;
  return (
    <span className="issue-label" style={style} title={title}>
      {name}
      {children}
    </span>
  );
}

/** Black or white, whichever reads on a six-digit hex fill (the W3C relative-luminance rule). */
export function contrastOn(hex: string): string {
  const n = parseInt(hex, 16);
  const channel = (c: number) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  };
  const l = 0.2126 * channel((n >> 16) & 255) + 0.7152 * channel((n >> 8) & 255) + 0.0722 * channel(n & 255);
  return l > 0.4 ? '#1f2328' : '#ffffff';
}

export type PullState = 'open' | 'draft' | 'merged' | 'closed';

/** GitHub's State badge: the pull request's state with its icon and colour. */
export function StateBadge({ state, size = 'medium' }: { state: PullState; size?: 'small' | 'medium' }) {
  const look: Record<PullState, { icon: IconName; text: string }> = {
    open: { icon: 'git-pull-request', text: 'Open' },
    draft: { icon: 'git-pull-request-draft', text: 'Draft' },
    merged: { icon: 'git-merge', text: 'Merged' },
    closed: { icon: 'git-pull-request-closed', text: 'Closed' },
  };
  const { icon, text } = look[state];
  return (
    <span className={`state state-${state} state-${size}`}>
      <Icon name={icon} />
      {text}
    </span>
  );
}

/** Which badge a pull request wears, from the fields GitHub gives. */
export function pullState(p: { merged?: boolean; draft?: boolean; state?: string }): PullState {
  if (p.merged) {
    return 'merged';
  }
  if (p.state && p.state.toUpperCase() !== 'OPEN') {
    return 'closed';
  }
  return p.draft ? 'draft' : 'open';
}

/**
 * A person's picture, or their initial when GitHub gave none (the fake and anonymous readers
 * have no avatar URLs). Never a broken image: `onError` falls back to the letter.
 */
export function Avatar({ login, url, size = 20 }: { login: string; url?: string | null; size?: number }) {
  const letter = (login.trim()[0] ?? '?').toUpperCase();
  return url ? (
    <img className="avatar" src={url} alt="" width={size} height={size} style={{ width: size, height: size }} loading="lazy" />
  ) : (
    <span className="avatar avatar-letter" style={{ width: size, height: size, fontSize: Math.max(10, size * 0.5) }} aria-hidden="true">{letter}</span>
  );
}

/** An item of the underline nav under a repository's header or a pull request's title. */
export interface NavItem {
  id: string;
  label: string;
  to: string;
  icon?: IconName;
  count?: number | null;
}

export function UnderlineNav({ items, current, label }: { items: NavItem[]; current: string; label: string }) {
  return (
    <nav className="underline-nav" aria-label={label}>
      {items.map((item) => (
        <Link key={item.id} to={item.to} className={`underline-nav-item${item.id === current ? ' is-current' : ''}`} aria-current={item.id === current ? 'page' : undefined}>
          {item.icon && <Icon name={item.icon} className="underline-nav-icon" />}
          <span>{item.label}</span>
          {item.count !== undefined && item.count !== null && <Counter>{item.count}</Counter>}
        </Link>
      ))}
    </nav>
  );
}

/** GitHub's blankslate: an icon, a heading, a line of text, maybe an action. For every "pick a…" and "nothing yet". */
export function Blankslate({ icon, heading, children, action, spacious = false }: { icon?: IconName; heading: string; children?: ReactNode; action?: ReactNode; spacious?: boolean }) {
  return (
    <div className={`blankslate${spacious ? ' blankslate-spacious' : ''}`}>
      {icon && <Icon name={icon} size={24} className="blankslate-icon" />}
      <h3 className="blankslate-heading">{heading}</h3>
      {children && <p className="blankslate-text">{children}</p>}
      {action && <div className="blankslate-action">{action}</div>}
    </div>
  );
}

/** A flash: a banner saying something about the page. `role` follows what it says. */
export function Flash({ scheme = 'default', children, role }: { scheme?: 'default' | 'success' | 'warning' | 'danger'; children: ReactNode; role?: 'alert' | 'status' | 'note' }) {
  const icon: Record<string, IconName> = { default: 'info', success: 'check-circle', warning: 'alert', danger: 'alert' };
  return (
    <div className={`flash flash-${scheme}`} role={role ?? (scheme === 'danger' ? 'alert' : 'note')}>
      <Icon name={icon[scheme] ?? 'info'} className="flash-icon" />
      <div className="flash-body">{children}</div>
    </div>
  );
}

export function Spinner({ size = 16, label = 'Loading' }: { size?: number; label?: string }) {
  return (
    <svg className="spinner" viewBox="0 0 16 16" width={size} height={size} fill="none" role="img" aria-label={label}>
      <circle cx="8" cy="8" r="7" stroke="currentColor" strokeOpacity="0.25" strokeWidth="2" />
      <path d="M15 8a7 7 0 0 0-7-7" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </svg>
  );
}

/** "Loading…" as GitHub draws it: a spinner and a quiet line, with aria-busy for the tests. */
export function Loading({ what = 'Loading…' }: { what?: string }) {
  return (
    <div className="loading" aria-busy="true">
      <Spinner />
      <span className="text-muted">{what}</span>
    </div>
  );
}

/**
 * "3 days ago", with the full date and time in the tooltip, as GitHub's relative-time does:
 * recent things read as how long ago, old things as their date. `now` is injectable for tests.
 */
export function relativeWords(when: Date, now: Date = new Date()): string {
  const seconds = Math.round((now.getTime() - when.getTime()) / 1000);
  if (!Number.isFinite(seconds)) {
    return '';
  }
  const future = seconds < 0;
  const s = Math.abs(seconds);
  const unit = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
  let words: string;
  if (s < 45) {
    return future ? 'in a moment' : 'just now';
  } else if (s < 90) {
    words = '1 minute';
  } else if (s < 3600) {
    words = unit(Math.round(s / 60), 'minute');
  } else if (s < 86400) {
    words = unit(Math.round(s / 3600), 'hour');
  } else if (s < 86400 * 30) {
    words = unit(Math.round(s / 86400), 'day');
  } else {
    // Past a month GitHub writes the date itself, with the year only when it is not this one.
    const sameYear = when.getFullYear() === now.getFullYear();
    return when.toLocaleDateString(undefined, sameYear ? { month: 'short', day: 'numeric' } : { year: 'numeric', month: 'short', day: 'numeric' });
  }
  return future ? `in ${words}` : `${words} ago`;
}

export function RelativeTime({ when, now }: { when: string | Date; now?: Date }) {
  const date = typeof when === 'string' ? new Date(when) : when;
  if (Number.isNaN(date.getTime())) {
    return <span className="relative-time">{String(when)}</span>;
  }
  const words = relativeWords(date, now);
  // The date the server gave, shown on hover; "on Oct 1" rather than "on 2026-10-01" is not
  // what a reviewer wants to check, so the tooltip carries the full stamp.
  return <time className="relative-time" dateTime={date.toISOString()} title={date.toLocaleString()}>{words}</time>;
}

/** A page's main column and its right sidebar, GitHub's two-column layout. */
export function Layout({ main, sidebar, sidebarPosition = 'end' }: { main: ReactNode; sidebar?: ReactNode; sidebarPosition?: 'start' | 'end' }) {
  return (
    <div className={`layout layout-sidebar-${sidebarPosition}${sidebar ? '' : ' layout-no-sidebar'}`}>
      <div className="layout-main">{main}</div>
      {sidebar && <aside className="layout-sidebar">{sidebar}</aside>}
    </div>
  );
}

/** A section of a sidebar: a small heading, an optional gear-style action, the content. */
export function SidebarSection({ heading, action, children }: { heading: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="sidebar-section">
      <div className="sidebar-heading">
        <span>{heading}</span>
        {action}
      </div>
      <div className="sidebar-body">{children}</div>
    </section>
  );
}

/** A bordered box with a header row, GitHub's Box. */
export function Box({ header, children, className }: { header?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <div className={`box${className ? ` ${className}` : ''}`}>
      {header && <div className="box-header">{header}</div>}
      {children}
    </div>
  );
}

export { Icon };
export type { IconName };
