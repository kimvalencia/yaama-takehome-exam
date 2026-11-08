# AI Usage

## Tools

- **Claude Code** (VS Code extension, with the `superpowers` plugin): design discussion, planning and implementation.

_(To be expanded as the work progresses: what each tool was used for.)_

## Prompts

### Prompt 1: kick-off and design discussion

Invoked with the `superpowers:brainstorming` skill so the design is discussed before any code is written.

> /superpowers:brainstorming read this file and lets discuss how to achieve this. include this in AI_USAGE.md as my first prompt

(The "file" is `candidate-brief_8155.md`, the take-home brief.)

**Why I started this way:** the brief leaves most decisions open (database, messaging, auth provider, API shape). I wanted to agree on the approach and trade-offs with the AI before generating code, instead of letting it pick defaults.

## Overrides

- **Database location:** the AI proposed (and I first accepted) connecting the containers to my local SQL Server via `host.docker.internal`. I reversed that: SQL Server runs as a container in the compose file instead. A reviewer would otherwise need their own SQL Server just to run `docker compose up`, which breaks the brief's "one command to run it".

_(More to be added as the work progresses.)_

## Trade-offs

_TODO_

## Verification

_TODO_
