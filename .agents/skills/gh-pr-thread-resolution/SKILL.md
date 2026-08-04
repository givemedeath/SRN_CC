---
name: gh-pr-thread-resolution
description: Fetch and resolve GitHub Pull Request review threads via GitHub GraphQL API using gh CLI
---

# GitHub PR Review Thread Resolution

Use this skill when tasked with inspecting, addressing, or marking GitHub Pull Request review threads as resolved.

## 1. Fetch Active Review Threads

To retrieve unresolved PR review threads and their thread IDs:

```bash
gh api graphql -F owner=<owner> -F name=<repo> -F pr=<pr_number> -f query='
query($owner: String!, $name: String!, $pr: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $pr) {
      reviewThreads(first: 100) {
        nodes {
          id
          isResolved
          comments(first: 1) {
            nodes {
              body
            }
          }
        }
      }
    }
  }
}'
```

## 2. Resolve Review Threads via GraphQL Mutation

> **IMPORTANT:** Only resolve a review thread after all changes related to the thread are completed, committed, and pushed to the repository. 

To resolve a review thread by its `threadId` (e.g. `PRRT_kw...`):

```bash
gh api graphql -F threadId=<thread_id> -f query='
mutation($threadId: ID!) {
  resolveReviewThread(input: {threadId: $threadId}) {
    thread {
      id
      isResolved
    }
  }
}'
```

## 3. Batch Resolution via PowerShell Script

When resolving multiple threads at once, write a temporary PowerShell script to avoid shell variable escaping syntax issues:

```powershell
$threads = @(
    'PRRT_kw...',
    'PRRT_kw...'
)

$mutation = @'
mutation($threadId: ID!) {
  resolveReviewThread(input: {threadId: $threadId}) {
    thread {
      id
      isResolved
    }
  }
}
'@

foreach ($t in $threads) {
    gh api graphql -F threadId=$t -f query=$mutation
}
```
