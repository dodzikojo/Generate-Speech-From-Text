## Choose the output language

@action: Select English, then briefly show the current language list and return to English.

The output language can differ from the Navisworks interface language. Current choices include English and ten other supported languages shown in the dropdown.

AI-written titles follow the chosen output language. Token formats are applied exactly as typed and are never translated. Source property values are inserted as they exist in the model, so a French output setting does not automatically translate a Revit type name stored in English.

Choose a model that supports the selected language and verify a small preview before processing a large test.

## Decide where the text belongs

@screen: Compare Title, Description, and Comments as the Update Option.

Title replaces the visible clash result name and is the normal coordination choice. Description preserves the current title and writes the generated text into the description field. Comments appends a comment while keeping title and description unchanged.

Title and Description overwrite the target field when applied. Comments add a new entry and can accumulate across runs. Know what already exists before choosing the destination.

Include Original Clash Title and Include Clash Test Name can prefix extra context, but both increase length. Use them only when the reference value is genuinely needed.

## Choose one destination by downstream use

Write to Title when the new name should become the primary label in Clash Detective. Write to Description when an existing naming convention must remain visible but the generated explanation should travel with the clash. Write to Comments when you need an additive review note rather than a replacement.

Comments accumulate. Repeating the same run can create several entries, so check the current comment history before applying another batch.

For multilingual work, target language changes the generated wording, not the source property values or the meaning of custom template tokens. Test abbreviations, level names, and discipline terms with a native reviewer before establishing the convention.

## Compare standards against one fixed sample

@action: Cycle the prepared clash through Basic, Detailed, Component I D, Discipline, A I Preferred, and Mixed previews without applying.

Basic favors compact readable names. Detailed carries more component context. Component I D prioritizes identifiers when those are the team's shared reference. Discipline emphasizes the two systems. A I Preferred lets the local model compose the label more freely. Mixed combines deterministic structure with generated wording.

The best standard is the one a coordinator can scan at speed and a contractor can understand without opening every viewpoint. Choose it from evidence, then save the convention for the project.
