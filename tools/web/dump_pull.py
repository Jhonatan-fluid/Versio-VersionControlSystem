"""Dumps one real pull request for the Versio Web preview: trees at base and head, and its review data.

Usage: python tools/web/dump_pull.py <owner> <repo> <number> <clone dir> <out dir>
Read-only: only GET requests and GraphQL queries, through the gh CLI as whoever it is signed in as.
The clone needs the pull request's head: git -C <clone dir> fetch origin pull/<number>/head.
Then: VERSIO_WEB_PREVIEW_PULL=<out dir> dotnet test tests/Versio.Web.Tests --filter PullPreview
(tests/Versio.Web.Tests/PullPreview.cs). The dump is the repository's own content: keep it out of git.
"""
import json, os, subprocess, sys

owner, name, number, clone, out = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4], sys.argv[5]


def gh(*args):
    return json.loads(subprocess.run(['gh', 'api', *args], capture_output=True, check=True).stdout.decode('utf-8'))


def gh_pages(path):
    items = []
    page = 1
    while True:
        batch = gh(f'{path}{"&" if "?" in path else "?"}per_page=100&page={page}')
        items += batch
        if len(batch) < 100:
            return items
        page += 1


def graphql(query, **variables):
    args = ['graphql', '-f', f'query={query}']
    for k, v in variables.items():
        args += ['-F', f'{k}={v}'] if isinstance(v, int) else ['-f', f'{k}={v}']
    return gh(*args)['data']


pull = gh(f'repos/{owner}/{name}/pulls/{number}')
base, head = pull['base']['sha'], pull['head']['sha']
merge_base = subprocess.run(['git', '-C', clone, 'merge-base', base, head], capture_output=True, check=True).stdout.decode().strip()


def tree(sha, into):
    # By object id, never "rev:path": these paths hold "^", which rev syntax reads as an operator.
    entries = subprocess.run(['git', '-C', clone, 'ls-tree', '-r', '-z', sha], capture_output=True, check=True).stdout.decode('utf-8').split('\0')
    # Blobs are stored flat by id: the real paths are long and Windows checkouts trip on them.
    os.makedirs(os.path.join(out, 'blobs'), exist_ok=True)
    lines = []
    for e in filter(None, entries):
        meta, n = e.split('\t', 1)
        _, kind, oid = meta.split()
        if kind != 'blob':
            continue
        blob = os.path.join(out, 'blobs', oid)
        if not os.path.exists(blob):
            data = subprocess.run(['git', '-C', clone, 'cat-file', 'blob', oid], capture_output=True, check=True).stdout
            with open(blob, 'wb') as f:
                f.write(data)
        lines.append(oid + '\t' + n + '\n')
    with open(os.path.join(out, into + '.tree'), 'w', encoding='utf-8', newline='\n') as f:
        f.writelines(lines)
    return len(lines)


counts = {'base': tree(base, 'base'), 'head': tree(head, 'head')}
if merge_base != base:
    counts['merge-base'] = tree(merge_base, 'merge-base')

files = gh_pages(f'repos/{owner}/{name}/pulls/{number}/files')
commits = gh_pages(f'repos/{owner}/{name}/pulls/{number}/commits')
comments = gh_pages(f'repos/{owner}/{name}/issues/{number}/comments')
reviews = gh_pages(f'repos/{owner}/{name}/pulls/{number}/reviews')
checks = gh(f'repos/{owner}/{name}/commits/{head}/check-runs?per_page=100')['check_runs']
statuses = gh(f'repos/{owner}/{name}/commits/{head}/status')['statuses']
labels = gh_pages(f'repos/{owner}/{name}/labels')

threads, cursor = [], None
q = '''query($o:String!,$n:String!,$p:Int!,$c:String){repository(owner:$o,name:$n){pullRequest(number:$p){
  reviewThreads(first:100,after:$c){pageInfo{hasNextPage endCursor} nodes{id path isResolved isOutdated line startLine diffSide originalLine subjectType
    comments(first:100){nodes{id author{login} body createdAt commit{oid} originalCommit{oid}}}}}}}}'''
while True:
    vars_ = {'o': owner, 'n': name, 'p': number}
    if cursor:
        vars_['c'] = cursor
    page = graphql(q, **vars_)['repository']['pullRequest']['reviewThreads']
    threads += page['nodes']
    if not page['pageInfo']['hasNextPage']:
        break
    cursor = page['pageInfo']['endCursor']

dump = {
    'owner': owner, 'name': name, 'number': number,
    'title': pull['title'], 'body': pull['body'] or '', 'author': pull['user']['login'],
    'created': pull['created_at'], 'updated': pull['updated_at'], 'draft': pull['draft'], 'state': pull['state'].upper(),
    'baseRef': pull['base']['ref'], 'headRef': pull['head']['ref'], 'base': base, 'head': head, 'mergeBase': merge_base,
    'labels': [{'name': l['name'], 'color': l['color']} for l in pull['labels']],
    'repoLabels': [{'name': l['name'], 'color': l['color']} for l in labels],
    'assignees': [a['login'] for a in pull['assignees']],
    'requested': [r['login'] for r in pull['requested_reviewers']],
    'files': [{'path': f['filename'], 'status': f['status'], 'previous': f.get('previous_filename'), 'patch': f.get('patch')} for f in files],
    'commits': [{'sha': c['sha'], 'headline': c['commit']['message'].split('\n')[0], 'author': (c['author'] or {}).get('login') or c['commit']['author']['name'], 'when': c['commit']['author']['date']} for c in commits],
    'comments': [{'id': str(c['id']), 'author': c['user']['login'], 'body': c['body'] or '', 'when': c['created_at']} for c in comments],
    'reviews': [{'id': str(r['id']), 'author': r['user']['login'], 'state': r['state'], 'body': r['body'] or '', 'when': r.get('submitted_at'), 'commit': r.get('commit_id')} for r in reviews],
    'checks': [{'name': c['name'], 'status': c['status'], 'conclusion': c['conclusion'], 'url': c['html_url'], 'source': 'check-run'} for c in checks]
              + [{'name': s['context'], 'status': 'completed' if s['state'] != 'pending' else 'in_progress', 'conclusion': None if s['state'] == 'pending' else s['state'], 'url': s['target_url'], 'source': 'status'} for s in statuses],
    'threads': [{'id': t['id'], 'path': t['path'], 'resolved': t['isResolved'], 'outdated': t['isOutdated'], 'line': t['line'], 'startLine': t['startLine'],
                 'side': t['diffSide'], 'originalLine': t['originalLine'], 'subjectType': t['subjectType'],
                 'comments': [{'id': c['id'], 'author': (c['author'] or {}).get('login', 'ghost'), 'body': c['body'], 'when': c['createdAt'],
                               'commit': (c['commit'] or {}).get('oid'), 'originalCommit': (c['originalCommit'] or {}).get('oid')} for c in t['comments']['nodes']]}
                for t in threads],
}
with open(os.path.join(out, 'pull.json'), 'w', encoding='utf-8', newline='\n') as f:
    json.dump(dump, f, indent=1, ensure_ascii=False)
print(json.dumps({'trees': counts, 'files': len(files), 'withPatch': sum(1 for f in files if f.get('patch')), 'commits': len(commits),
                  'comments': len(comments), 'reviews': len(reviews), 'threads': len(threads), 'checks': len(dump['checks']), 'labels': len(dump['labels'])}))
