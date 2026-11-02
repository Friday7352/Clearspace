from pathlib import Path
import re

for name in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/name
    p=root/'index.html';html=p.read_text(encoding='utf-8')
    html=html.replace('My professional IT support work and Unity collaboration provide experience coordinating responsibilities and communicating with contributors. The self-assessment explains this background; Clearspace itself was developed independently.', 'The self-assessment describes my professional IT support background and a Unity project where I led development and another contributor provided music. Clearspace itself was developed independently.')
    html=html.replace('The three enhancement narratives explain purpose, implementation choices, verification, and limitations. The portfolio presents these explanations alongside source and evidence.', 'The code review and three enhancement narratives explain purpose, implementation choices, verification, and limitations. The self-assessment discusses adapting technical explanations for instructors, developers, and application users.')
    html=html.replace('C#, WPF, indexed data structures, SQLite, and regression tests turn the enhancement plans into working features.', 'C#, WPF, indexed data structures, SQLite, and regression tests turn the enhancement plans into working features. The self-assessment also connects this work to CS 320, including JUnit tests for Contact, Task, and Appointment services and reported code coverage over 80 percent.')
    html=html.replace('and databases. It is written for peers or a manager, so it explains how the code works and', 'and databases. It is intended for peers or a manager, so it explains how the code works and')
    html=html.replace('the review is addressed to peers or a manager and invites feedback on the plan before the changes are made.', 'the review communicates an enhancement plan that peers or a manager can evaluate. Clearspace was developed independently; the self-assessment provides separate professional and Unity collaboration experience.')
    note='<p class="journal-context">These dated journals record progress at the time they were written. The self-assessment, artifact sections, and code-review link above show the current portfolio.</p>'
    marker='<h2>Journals and Progress</h2>'
    if 'class="journal-context"' not in html:html=html.replace(marker,marker+'\n    '+note)
    p.write_text(html,encoding='utf-8')
    for filename in ('career-reflection.html','module-five-journal.html'):
        p=root/filename;html=p.read_text(encoding='utf-8')
        if 'Historical progress entry' not in html:
            note='<aside aria-label="Historical progress entry"><p><strong>Historical progress entry:</strong> This journal describes the project at its original date. Later work, the code-review video, and the final self-assessment are available on the <a href="index.html">current portfolio homepage</a>.</p></aside>'
            html=html.replace('<main>','<main>'+note,1)
            p.write_text(html,encoding='utf-8')
    print(name,'accuracy summaries and historical labels updated')
