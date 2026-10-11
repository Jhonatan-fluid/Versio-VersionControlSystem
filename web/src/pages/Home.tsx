import { useState } from 'react';
import { useNavigate } from 'react-router';
import type { Me } from '../api';
import { Header } from '../components/Header';
import { parseRepoInput, repoUrl } from '../routes';

export function Home({ me }: { me: Me | null }) {
  const navigate = useNavigate();
  const [input, setInput] = useState('');
  const [error, setError] = useState<string | null>(null);
  const failed = new URLSearchParams(window.location.search).get('signin') === 'failed';

  return (
    <div className="app">
      <Header me={me} />
      <main className="home">
        <h1>Open a repository</h1>
        <p className="muted">
          See a PLC project the way Versio shows it: data types and interfaces as tables, ladder and FBD as networks, Structured Text
          numbered as the IDE numbers it. View only.
        </p>
        {failed && <p className="notice" role="alert">Sign-in did not complete. Try again.</p>}
        <form
          className="open-form"
          onSubmit={(e) => {
            e.preventDefault();
            const parsed = parseRepoInput(input);
            if (!parsed) {
              setError('Enter owner/repository, or a github.com link to one.');
              return;
            }
            navigate(repoUrl(parsed.owner, parsed.repo));
          }}
        >
          <label htmlFor="repo">Repository</label>
          <div className="open-row">
            <input id="repo" value={input} onChange={(e) => setInput(e.target.value)} placeholder="owner/repository" autoComplete="off" spellCheck={false} />
            <button type="submit" className="button">Open</button>
          </div>
          {error && <p className="error-text">{error}</p>}
        </form>
        {me && !me.signedIn && (
          <p className="muted">
            {me.signInAvailable ? 'Public repositories open without signing in. Sign in to open private ones.' : 'Public repositories only: sign-in is not configured on this server.'}
          </p>
        )}
      </main>
    </div>
  );
}
