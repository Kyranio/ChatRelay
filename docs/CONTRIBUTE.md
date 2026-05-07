# Contributions

We welcome contributions to this project! If you have an idea for a new feature, have found a bug, or want to improve the documentation, please feel free to submit a pull request.
Before you start working on a contribution, make sure to read the [DEVELOPMENT](DEVELOPMENT.md) guide for set-up and best practices.

## Prerequisites

- **.NET SDK** fitting the current version used in the core project
- **IDE** of your choice. _In case you wish to work on a (new) extension for a specific IDE, it's recommended to use that IDE!_
- **Workloads/Libraries/Components/etc** required for your IDE and the project. _(e.g., Visual Studio extension development workload for Visual Studio)_
- **GitHub account** with push access to a fork or a feature branch on this repo.

## 1. Fork & clone

First, fork this repo and make any changes on a branch in your fork. 
(You can do this in any way you like, as long as the fork will become available on GitHub **to be able to create a pullrequest**).

**Bash example:**
```bash
git clone https://github.com/<you>/ChatRelay.git
cd ChatRelay
git remote add upstream https://github.com/Kyranio/ChatRelay.git
```

## 2. Branching

New branches must be based off the latest `dev` branch.
Branch naming is flexible but please choose something descriptive and consistent, such as:
* `feature/<short-name>` for new features
* `fix/<short-name>` for bug fixes
* `docs/<short-name>` for documentation improvements
* `refactor/<short-name>` for code refactoring (without changing functionality)

## 3. Commit messages

Be descriptive, yet brief. A good commit message should explain the "what" and "why" of the change, not just the "how". For example:
```
Added support for coffee machines:

- Implemented the new brewing algorithm in the CoffeeMachine class.
- Updated the user interface to include coffee options.
- Added unit tests for the new functionality.

This change allows users to brew coffee directly from the app, which improves the overall user experience, and will rapidly increase our overall user base.
The brewing algorithm was chosen for its efficiency and compatibility with our existing architecture.
```

## 4. Pull requests

All contributions must be made through pull requests.
Every PR must target the `dev` branch.
Pull requests to `master` must come from `dev` only, and are maintained by the project owner.

### PR Template
When creating a pull request, please use the following template to provide necessary information:
```markdown
# <Title of PR>

A brief description of the changes made and the problem it solves.
- Why is this change necessary?
- What problem does it solve?
- How does it solve the problem?

## Related Issues (if any)
- [Issue #12345](https://link-to-issue)
```

> ➡️ Direct pushes to `master` and `dev` are blocked.

## Reporting issues
If you encounter a bug or have a suggestion for improvement, please open an issue on GitHub.
Inside the issue, please provide as much detail as possible, including:
- Steps to reproduce the issue
- The expected behavior vs. the actual behavior
- What you think might be causing the issue (if you have an idea)
- Any relevant screenshots or logs
- What you think would be a good solution, or how this could be improved