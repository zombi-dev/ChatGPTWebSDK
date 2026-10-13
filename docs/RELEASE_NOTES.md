# v1.7.1

Document and ZIP attachments now work through the ChatGPT web backend using the original C# SDK file types. Keep the browser-free `ApiOnly` default introduced in v1.7.0; this update extends the shared file adapter used by the drop-in SDK, native SDK and proxy.

## Files and compatibility

- Support Chat Completions `file` parts and Responses `input_file` parts with inline base64/data URIs or an uploaded file ID. Preserve the original client signatures and OpenAI compatibility assembly version **2.14.0.0**.
- Upload documents as `my_files`, ZIP archives as `ace_upload` and images as `multimodal`. Use the same category and resolved MIME type for creation, signed storage upload and processing.
- Infer MIME types from common document, spreadsheet, code, image and archive extensions, including uppercase filenames. Explicit MIME types take precedence; JPEGs retain `image/jpeg`.
- Represent documents as metadata attachments with text, rather than synthetic image pointers or zero image dimensions. Mixed image/document messages keep the existing image pointer format alongside file metadata.

## Validation and state

- Validate every content part and file reference within a message before starting uploads. Reject malformed base64, conflicting sources, invalid MIME values, remote file URLs and unsupported rendering-detail overrides explicitly.
- Retain account/application-user ownership checks, bounded uploads, signed-host credential isolation, cached history attachments and append-only continuation. Collected replies and SSE share the same behavior in ordinary, project and temporary chats.
- Keep the existing native Sentinel decoder and fresh-proof handling. The reviewed fork adds no newer solver; its external-host authorization and broken image callers were not adopted.

## Verification and downloads

- Add **55 core/drop-in cases** and **four native integration cases**. The build gate requires **1,639 core/drop-in**, **26 native**, **168 automation** and **12 extension** checks before packaging.
- Signed-in live tests uploaded and processed a PDF and an uppercase `.ZIP` archive, then ChatGPT recovered a private synthetic marker from each file through Responses file references. Both test conversations were deleted. The backend rejected direct file deletion with HTTP 404, so no remote file-purge claim is made.
- Synchronize SDK packages, browser extensions, DLL/proxy archives and browser bundles at **1.7.1**. No new runtime dependency is required.

See the [file examples](https://github.com/zombi-dev/ChatGPTWebSDK/blob/v1.7.1/README.md#documents-and-zip-attachments), [reference review](https://github.com/zombi-dev/ChatGPTWebSDK/blob/v1.7.1/docs/REFERENCES.md) and [verification record](https://github.com/zombi-dev/ChatGPTWebSDK/blob/v1.7.1/docs/VERIFICATION.md). All commits are linked in the collapsed appendix below.
