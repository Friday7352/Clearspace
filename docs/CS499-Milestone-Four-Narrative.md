# Clearspace Database Enhancement Narrative

Payton Castle | CS 499 Milestone Four | October 1, 2026

## Artifact background

For this milestone, I enhanced Clearspace with SQLite storage for tags and password-protected files. I created Clearspace in August 2026 as a personal Windows file-manager project using C# and Windows Presentation Foundation. My Module One plan records instructor approval to use the project. Clearspace supports file navigation, search, tags, and file operations. This submission includes the source before the database enhancement and the enhanced application, together with test evidence.

## Why I selected this artifact and how it improved

I selected Clearspace because its stored metadata affects a real user workflow. Previously, tag definitions and assignments were saved in one JSON document. Each change rewrote the document, and the application maintained relationships itself. Moving that data into SQLite let me demonstrate relational design, indexed queries, and transactions within an application I could use and test.

The database separates Tags, Paths, and PathTags. Primary keys prevent duplicate assignments, foreign keys prevent orphaned references, and an index supports finding paths by tag. Bulk changes commit together or roll back together. The first-run migration imports the existing JSON transactionally and retains the original file. The code reports invalid input instead of silently replacing a user's metadata. Tag lookups also use an in-memory snapshot so displaying many results does not require a database query for each file.

The enhancement now includes LockSettings and LockedFiles. LockSettings stores a salted master-password verifier. LockedFiles stores paths, file identifiers, operation states, and a versioned header containing wrapped keys and authentication information. Schema version 2 adds these tables without discarding existing tags. Parameterized statements keep tag names and file paths separate from SQL instructions.

The file-lock feature makes this storage useful. A user can right-click a file, enter a password, and encrypt its contents. Each file receives a random AES-256-GCM data key, protected by a password-derived key. Password derivation uses PBKDF2-HMAC-SHA256 with a random salt and 600,000 iterations. This follows the planned use of established cryptographic tools and the documented requirements for GCM nonces and password derivation (Microsoft, n.d.; OWASP Foundation, n.d.). The database never stores a plaintext password or unwrapped file key. The file also carries its protected header so a rename or loss of the local database does not make recovery depend on the old path record.

## Course outcomes and updates to my plan

My Module One database plan selected Outcomes 4, 5, and 3. Outcome 4 concerned using established computing tools to deliver useful software. SQLite, explicit schema upgrades, .NET cryptography, and automated tests provide evidence for that outcome. The completed work replaces the tag-storage approach and implements the planned lock-metadata and password-verifier storage.

For Outcome 5, I addressed specific threats and failure cases. Bound SQL parameters prevent user values from becoming query instructions. Authenticated encryption rejects wrong passwords and changed ciphertext. The service clears its plaintext and key buffers after use, and temporary files receive restricted Windows permissions. Tests exercise malformed headers, database failures, and interrupted operations. These measures demonstrate progress toward a security mindset; they do not establish that the application has undergone an independent security audit.

Outcome 3 concerns design tradeoffs. SQLite adds schema and migration complexity but provides constraints and transactions. Caching reduces repeated database work while requiring refresh rules. Duplicating protected recovery metadata in the encrypted file uses a small amount of extra space but removes dependence on a particular database or filename. Limiting the first implementation to files up to 64 MiB bounds the memory required to authenticate an entire file before replacing it.

The initial September 29 implementation covered only tags. Revisiting Module One showed that file locking was originally planned under software engineering and its metadata under databases. I have now included both in this milestone. The first version supports one writable local NTFS file at a time; folders, links, cloud placeholders, and files with additional data streams are excluded. These limits narrow the original idea of locking any file and are stated for instructor review. The narrative and technical notes also support Outcome 2. Evidence for Outcome 1 still depends on documenting how I use instructor or peer feedback; automated checks alone do not demonstrate collaboration.

## Reflection on learning and challenges

The most useful lesson was that successful encryption is only one part of preserving a user's files. A database transaction cannot also commit a Windows file replacement. The implementation therefore records recovery metadata first, prepares and verifies a temporary file, replaces the target, and then updates its final database state. The encrypted file's header remains usable if the last database update fails. This required thinking about what remains on disk at each failure point.

Migration also required preserving behavior. The application compared Unicode paths without case sensitivity, so the database registers a matching collation. Tests cover old data, duplicate assignments, rollback, and concurrent access. The schema upgrade adds lock tables without importing the JSON again or replacing existing tag data.

The full Release suite passed 251 tests on October 1, 2026. A separate check exercised the actual password-dialog buttons with disposable files, including a wrong-password attempt, and confirmed that unlocking restored the original contents. These checks strengthened the evidence beyond compilation. They also clarified the limits: injected interruptions do not reproduce a physical power loss, and encryption does not erase existing backups or cached copies. A crash during unlock preparation can leave a temporary plaintext file restricted to the current Windows user and SYSTEM. Those boundaries are documented so the portfolio explains what was verified and what still requires further work.

## References

Microsoft. (n.d.). AesGcm.Encrypt method. Microsoft Learn. https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-10.0

OWASP Foundation. (n.d.). Password storage cheat sheet. OWASP Cheat Sheet Series. https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html
