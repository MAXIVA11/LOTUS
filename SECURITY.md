# Security policy

LOTUS is a security product, so a weakness in it matters more than a weakness in most programs. If you find one, please report it in private first.

## How to report

Use GitHub's private report form: [Report a vulnerability](https://github.com/MAXIVA11/LOTUS/security/advisories/new). Only the maintainer sees what you write there.

Please do not open a public issue for a weakness that someone could use against a PC that runs LOTUS. Ordinary bugs, such as a device that is named wrongly or a button that does nothing, belong in the [issue list](https://github.com/MAXIVA11/LOTUS/issues).

A report is most useful when it says:

- the LOTUS version and the system it ran on (Windows build, or Linux distribution and kernel)
- what you did, step by step, and what happened
- what someone gains by it: a blocked device that works, a changed policy, a stopped service, or something else
- which rights the attacker needs: none, a signed-in user, an administrator, or hands on the hardware

## What happens next

The maintainer answers in the report itself. A confirmed weakness is fixed in a new release, and the release notes name it and thank you, unless you would rather stay unnamed. Please give the fix time to reach a release before you publish the details.

## Which versions get fixes

Only the newest release. LOTUS has no long-term branches, so a fix always arrives as a new version, and the installer updates an older one in place.

## What is already known

[docs/security.md](docs/security.md) describes who LOTUS is built to stop and where its protection ends. Those limits are documented behaviour. Among them:

- an administrator can change the policy or remove LOTUS
- a device that copies the vendor, product, serial number and functions of a trusted device is treated as that device
- someone who can start the PC from other media, or open it, is outside what LOTUS can stop

A way around LOTUS that needs fewer rights than those limits say is a weakness, and worth a report.
