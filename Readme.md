# Delete links as they happen
Tool to delete links created by Microsoft Teams, Microsoft OneClick, and Microsoft OneDrive when using OneDrive for Roaming Profiles in combination with Known Folder Redirection.

Monitors CSIDL_DesktopDirectory and deletes any file ending in .lnk or .appref-ms.  Stays running and continues deleting links throughout the session.  Also responds to known folders being redirected by OneDrive.