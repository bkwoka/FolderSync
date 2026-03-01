#!/usr/bin/env python3
"""
FolderSync Resource Automation Utility
Orchestrates the bulk update of .resx localization files from a structured text input.
Ensures consistency between English (RESX_EN) and Polish (RESX_PL) resources.
"""
import xml.etree.ElementTree as ET
import os
import sys

# PATH CONFIGURATION: Relative paths ensure portability across development environments.
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
INPUT_FILE = os.path.join(SCRIPT_DIR, "translations.txt")

# RESOURCE MAPPING: Paths leading to the project's localization hubs.
RESX_EN = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "src", "FolderSync", "Resources", "AppStrings.resx"))
RESX_PL = os.path.abspath(os.path.join(SCRIPT_DIR, "..", "src", "FolderSync", "Resources", "AppStrings.pl.resx"))

def main():
    """
    Primary entry point: parses the translation manifest and updates the resource files.
    """
    if not os.path.exists(INPUT_FILE):
        print(f"ERROR: Input translation manifest not found: {INPUT_FILE}")
        print("Please create 'translations.txt' in the tools directory with the required keys.")
        sys.exit(1)

    with open(INPUT_FILE, "r", encoding="utf-8") as f:
        lines = f.readlines()

    keys = []
    start = False
    for line in lines:
        if line.startswith("Key | English | Polish"):
            start = True
            continue
        if start and "|" in line:
            parts = [p.strip() for p in line.split("|")]
            if len(parts) >= 3:
                k = parts[0]
                if k == "Key": continue
                # Handle explicit newline escape sequences for multiline strings
                v1 = parts[1].replace("\\n", "\n")
                v2 = parts[2].replace("\\n", "\n")
                keys.append((k, v1, v2))

    if not keys:
        print("WARNING: No localization keys detected in translations.txt.")
        return

    # Update individual resource files
    add_to_resx(RESX_EN, 1, keys)
    add_to_resx(RESX_PL, 2, keys)
    
    print("SUCCESS: Localization updates synchronized successfully.")

def add_to_resx(filepath, index, keys):
    """
    Injects new keys into a specific .resx XML structure while preserving existing data.
    """
    if not os.path.exists(filepath):
        print(f"ERROR: Resource file missing at: {filepath}")
        return
        
    tree = ET.parse(filepath)
    root = tree.getroot()
    existing_nodes = root.findall('data')
    existing_keys = set([d.attrib['name'] for d in existing_nodes])
    
    added = 0
    updated = 0
    
    # Create a mapping for quick lookup
    data_nodes = {d.attrib['name']: d for d in existing_nodes}
    
    for k, v1, v2 in keys:
        new_val = v1 if index == 1 else v2
        if k in data_nodes:
            val_node = data_nodes[k].find('value')
            if val_node is not None:
                val_node.text = new_val
                updated += 1
            continue
            
        # Construct .resx compliant XML element
        data = ET.SubElement(root, 'data')
        data.set('name', k)
        data.set('xml:space', 'preserve')
        val = ET.SubElement(data, 'value')
        val.text = new_val
        added += 1
    
    # Write to file with explicit XML declaration for .NET compatibility
    xml_str = ET.tostring(root, encoding='utf-8', method='xml').decode('utf-8')
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write('<?xml version="1.0" encoding="utf-8"?>\n' + xml_str)
        
    print(f" [+] Synchronized {added} new / {updated} updated keys with: {os.path.basename(filepath)}")

if __name__ == "__main__":
    main()
