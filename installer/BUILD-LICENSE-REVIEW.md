# Inno Setup build permission review

Checked 2026-09-07 for the unmodified official Inno Setup 6.7.3 compiler used by DLB Precision Monitor.

The compiler prints **Non-commercial use only** without a paid key. The actual license shipped in that same signed compiler package grants use for any purpose, including commercial applications, subject to preserving its notices and not misrepresenting its origin or modifications. This exact license is retained in `licenses/Inno-Setup-LICENSE.txt`. [Publisher's license text](https://jrsoftware.org/files/is/license.txt)

The publisher's FAQ directly asks whether commercial users must purchase a license and answers: **“It is not strictly required.”** The same FAQ says a license is not expected while an installer is being developed before production readiness. It requests support from commercial users, including for older versions, so changing to an older release does not avoid that request. [Publisher's commercial-license FAQ](https://jrsoftware.org/isorder.php)

Based on those explicit permissions, this private pilot can be built with the current compiler without buying or registering a key. The written license also permits commercial distribution subject to its conditions. DLB can decide whether to support the publisher before customer rollout; no purchase or registration has been performed or assumed here. The compiler and generated setup retain upstream notices, and the installed product includes attribution/license files. Recheck the actual license if upgrading the compiler.

Keep the pinned 6.7.3 release rather than downgrading for licensing reasons. The current branch contains security-related installer improvements described in the [official release history](https://jrsoftware.org/files/is6-whatsnew.htm); there is no permission advantage to using an old compiler under the currently published terms.
