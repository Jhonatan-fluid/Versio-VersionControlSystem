import { useEffect, useRef, useState, type RefObject } from 'react';
import { Link } from 'react-router';
import type { Me } from '../api';
import { api } from '../api';
import { repoUrl } from '../routes';
import { applyColorMode, readColorMode, type ColorMode } from '../theme';
import { Avatar, Icon, LinkButton, VersioMark } from './ui';

interface Props {
  me: Me | null;
  /** The repository this page is about, for the breadcrumb; none on the home page. */
  repo?: { owner: string; name: string };
  /** What the page is about after the repository, as GitHub's breadcrumb ends ("Pull requests"). */
  crumb?: string;
}

/**
 * GitHub's global header, with Versio's mark where GitHub's would be: the octocat sat here
 * until the owner's first look (2026-10-24) — the page is shaped like github.com, and the
 * one thing that must say it is not github.com is the brand. Then the breadcrumb, and on the right the person — their
 * picture opening a menu with the theme and sign-out — or the way in. The branch picker that
 * used to live here belongs to the Code view and nowhere else; a pull request page with a
 * `main` picker in its header was one of the owner's findings (docs/web/FINISH.md, A12).
 */
export function AppHeader({ me, repo, crumb }: Props) {
  const here = window.location.pathname + window.location.search;
  return (
    <header className="app-header">
      <div className="app-header-inner">
        <Link to="/" className="app-header-mark" aria-label="Versio home">
          <VersioMark size={32} />
        </Link>
        <nav className="app-header-crumbs" aria-label="Where you are">
          {repo ? (
            <>
              <Link to={repoUrl(repo.owner, repo.name)} className="crumb crumb-owner">{repo.owner}</Link>
              <span className="crumb-sep" aria-hidden="true">/</span>
              <Link to={repoUrl(repo.owner, repo.name)} className="crumb crumb-repo">{repo.name}</Link>
              {crumb && (
                <>
                  <span className="crumb-sep" aria-hidden="true">/</span>
                  <span className="crumb crumb-page">{crumb}</span>
                </>
              )}
            </>
          ) : (
            <Link to="/" className="crumb crumb-repo">Versio</Link>
          )}
        </nav>
        <div className="app-header-spacer" />
        {me?.signedIn ? (
          <UserMenu me={me} />
        ) : (
          <>
            <ThemeMenu />
            {me?.signInAvailable && <LinkButton href={api.signInUrl(here)} size="small">Sign in</LinkButton>}
          </>
        )}
      </div>
    </header>
  );
}

/** The signed-in person's menu, as GitHub's avatar menu: who they are, the theme, sign out. */
function UserMenu({ me }: { me: Me }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useClickAway(ref, () => setOpen(false));
  return (
    <div className="menu-host" ref={ref}>
      <button type="button" className="avatar-button" onClick={() => setOpen(!open)} aria-haspopup="menu" aria-expanded={open} aria-label="Account menu">
        <Avatar login={me.login ?? '?'} url={me.avatarUrl} size={32} />
      </button>
      {open && (
        <div className="menu" role="menu">
          <div className="menu-head">
            <Avatar login={me.login ?? '?'} url={me.avatarUrl} size={20} />
            <span className="menu-login">{me.login}</span>
            {me.name && <span className="text-muted">{me.name}</span>}
          </div>
          <div className="menu-divider" />
          <ThemeItems />
          <div className="menu-divider" />
          <button
            type="button"
            role="menuitem"
            className="menu-item"
            onClick={async () => {
              await api.logout();
              window.location.assign('/');
            }}
          >
            <Icon name="sign-out" /> Sign out
          </button>
        </div>
      )}
    </div>
  );
}

/** The theme alone, for a signed-out visitor. */
function ThemeMenu() {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useClickAway(ref, () => setOpen(false));
  return (
    <div className="menu-host" ref={ref}>
      <button type="button" className="btn btn-invisible btn-small" onClick={() => setOpen(!open)} aria-haspopup="menu" aria-expanded={open} aria-label="Theme">
        <Icon name="sun" />
      </button>
      {open && (
        <div className="menu" role="menu">
          <ThemeItems />
        </div>
      )}
    </div>
  );
}

function ThemeItems() {
  const [mode, setMode] = useState<ColorMode>(() => readColorMode());
  const choices: { mode: ColorMode; label: string; icon: 'device-desktop' | 'sun' | 'moon' }[] = [
    { mode: 'system', label: 'Sync with system', icon: 'device-desktop' },
    { mode: 'light', label: 'Light', icon: 'sun' },
    { mode: 'dark', label: 'Dark', icon: 'moon' },
  ];
  return (
    <>
      <div className="menu-label">Theme</div>
      {choices.map((c) => (
        <button
          key={c.mode}
          type="button"
          role="menuitemradio"
          aria-checked={mode === c.mode}
          className="menu-item"
          onClick={() => {
            applyColorMode(c.mode);
            setMode(c.mode);
          }}
        >
          <Icon name={c.icon} /> {c.label}
          {mode === c.mode && <Icon name="check" className="menu-check" />}
        </button>
      ))}
    </>
  );
}

function useClickAway(ref: RefObject<HTMLElement | null>, onAway: () => void) {
  useEffect(() => {
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        onAway();
      }
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        onAway();
      }
    }
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [ref, onAway]);
}
