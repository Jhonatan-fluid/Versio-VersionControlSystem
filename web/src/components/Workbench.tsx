import type { ReactNode } from 'react';
import { useEffect, useState } from 'react';
import { Link, useLocation } from 'react-router';
import type { Branch, Me } from '../api';
import { compareUrl, historyUrl, pullsUrl, releasesUrl, treeUrl } from '../routes';
import { Header } from './Header';

/** Which rail tile is lit: the web's read-only views of one repository. */
export type RailChoice = 'project' | 'history' | 'compare' | 'pulls' | 'releases';

interface Props {
  me: Me | null;
  owner: string;
  repo: string;
  /** The version everything shown is at; on a comparison, the newer side. */
  refName: string;
  defaultBranch: string;
  branches: Branch[];
  branchesTruncated: boolean;
  onRef: (ref: string) => void;
  rail: RailChoice;
  /** The panel card's section label, like the desktop's PanelTitle. */
  panelTitle: string;
  panel: ReactNode;
  /** The pane. */
  children: ReactNode;
  /** The older side, so leaving Compare and coming back keeps the range. */
  compareBase?: string;
}

/**
 * The desktop shell, in a browser: a rail that swaps one panel card, and the pane beside it.
 * The desktop's rail has Project / Explorer / Git / History; here Git has no read-only meaning,
 * so Compare — what changed between two versions — takes its place, and Explorer waits until
 * somebody misses it (docs/web/PLAN.md, the restructure).
 */
export function Workbench({ me, owner, repo, refName, defaultBranch, branches, branchesTruncated, onRef, rail, panelTitle, panel, children, compareBase }: Props) {
  // Under 760 px the panel is a drawer over the pane; the header's ☰ opens it. The desktop
  // never has this problem — its window is never a phone.
  const [panelOpen, setPanelOpen] = useState(false);

  // Picking anything in the panel navigates, and on a phone the point of picking is to see the
  // pane — so any navigation closes the drawer. The pages used to close it themselves; the
  // state lives here now, and here is the one place every navigation passes through.
  const location = useLocation();
  useEffect(() => {
    setPanelOpen(false);
  }, [location.pathname, location.search]);

  return (
    <div className="app">
      <Header
        me={me}
        onToggleTree={() => setPanelOpen(!panelOpen)}
        repo={{ owner, name: repo, ref: refName, branches, branchesTruncated, onRef }}
      />
      <div className="workbench">
        <nav className="rail" aria-label="Views">
          <RailTile
            to={treeUrl(owner, repo, refName)}
            active={rail === 'project'}
            label="Project"
            tooltip="Project — the repository's objects and files"
          >
            {/* A document: the project as the toolchain structures it. */}
            <path d="M4 1.5h5.5L13 5v9.5H4z" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" />
            <path d="M9.5 1.5V5H13" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" />
          </RailTile>
          <RailTile
            to={historyUrl(owner, repo, refName)}
            active={rail === 'history'}
            label="History"
            tooltip="History — commits, each openable and comparable"
          >
            {/* A clock: what already happened. */}
            <circle cx="8.5" cy="8" r="6" fill="none" stroke="currentColor" strokeWidth="1.3" />
            <path d="M8.5 4.5V8l2.5 1.5" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          </RailTile>
          <RailTile
            to={compareUrl(owner, repo, compareBase ?? defaultBranch, refName)}
            active={rail === 'compare'}
            label="Compare"
            tooltip="Compare — what changed between two versions"
          >
            {/* Two opposed arrows: older against newer. */}
            <path d="M11 2.5 14 5l-3 2.5M14 5H5" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
            <path d="M6 8.5 3 11l3 2.5M3 11h9" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" />
          </RailTile>
          <RailTile
            to={pullsUrl(owner, repo)}
            active={rail === 'pulls'}
            label="Merge requests"
            tooltip="Merge requests — open pull requests, each with its diff"
          >
            {/* A branch merging: two dots, the head's line curving into the base's. */}
            <circle cx="4.5" cy="3.5" r="1.8" fill="none" stroke="currentColor" strokeWidth="1.3" />
            <circle cx="4.5" cy="12.5" r="1.8" fill="none" stroke="currentColor" strokeWidth="1.3" />
            <circle cx="12" cy="12.5" r="1.8" fill="none" stroke="currentColor" strokeWidth="1.3" />
            <path d="M4.5 5.3v5.4M6.5 4.5c3 0 5.5 2 5.5 6" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
          </RailTile>
          <RailTile
            to={releasesUrl(owner, repo)}
            active={rail === 'releases'}
            label="Releases"
            tooltip="Releases — published versions and their notes"
          >
            {/* A tag. */}
            <path d="M2 2h5.5L14 8.5 8.5 14 2 7.5z" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" />
            <circle cx="5.5" cy="5.5" r="1.1" fill="currentColor" />
          </RailTile>
        </nav>
        <div className={`workspace${panelOpen ? ' panel-open' : ''}`}>
          <aside className="panel">
            <div className="panel-title">{panelTitle}</div>
            <div className="panel-body">{panel}</div>
          </aside>
          {children}
        </div>
      </div>
    </div>
  );
}

function RailTile({ to, active, label, tooltip, children }: { to: string; active: boolean; label: string; tooltip: string; children: ReactNode }) {
  return (
    <Link to={to} className={`rail-tile${active ? ' is-active' : ''}`} title={tooltip} aria-current={active ? 'page' : undefined}>
      {/* No <title> inside the svg: it would join the link's text. The label is the
          visually-hidden span, which is also the accessible name. */}
      <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
        {children}
      </svg>
      <span className="visually-hidden">{label}</span>
    </Link>
  );
}
