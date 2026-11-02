// Prints a feature-level summary of the current program: architecture,
// function count, imports grouped by DLL, exports, and strings that hint at
// what the binary does. Meant for learning what a binary is built from,
// not for extracting its code.
//@category Learning
import java.util.*;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolTable;

public class SummarizeBinary extends GhidraScript {

	private static final String[] KEYWORDS = { "capture", "graphics", "window", "monitor",
		"snip", "clipboard", "ocr", "hotkey", "delay", "record", "screen", "bitmap", "png",
		"d3d", "dxgi", "cursor", "border", "rect" };

	@Override
	public void run() throws Exception {
		// Script arguments replace the default keyword list, e.g. -postScript SummarizeBinary.java button toolbar
		String[] keywords = getScriptArgs().length > 0 ? getScriptArgs() : KEYWORDS;
		println("=== " + currentProgram.getName() + " [" + currentProgram.getLanguageID() + "]");
		println("Functions: " + currentProgram.getFunctionManager().getFunctionCount());

		SymbolTable st = currentProgram.getSymbolTable();

		Map<String, List<String>> imports = new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
		for (Symbol s : st.getExternalSymbols()) {
			imports.computeIfAbsent(s.getParentNamespace().getName(), k -> new ArrayList<>())
					.add(s.getName());
		}
		println("--- Imports (" + imports.size() + " DLLs)");
		for (Map.Entry<String, List<String>> e : imports.entrySet()) {
			Collections.sort(e.getValue());
			println(e.getKey() + " (" + e.getValue().size() + "): " +
				String.join(", ", e.getValue()));
		}

		println("--- Exports");
		for (Address a : st.getExternalEntryPointIterator()) {
			Symbol s = st.getPrimarySymbol(a);
			println(a + "  " + (s != null ? s.getName() : "?"));
		}

		int total = 0;
		Set<String> hits = new TreeSet<>();
		for (Data d : currentProgram.getListing().getDefinedData(true)) {
			if (!d.hasStringValue()) {
				continue;
			}
			total++;
			String value = String.valueOf(d.getValue());
			String lower = value.toLowerCase();
			for (String k : keywords) {
				if (lower.contains(k.toLowerCase()) && value.length() < 160) {
					hits.add(value);
					break;
				}
			}
		}
		println("--- Strings: " + total + " total, " + hits.size() + " feature-related");
		hits.stream().limit(400).forEach(this::println);
	}
}
