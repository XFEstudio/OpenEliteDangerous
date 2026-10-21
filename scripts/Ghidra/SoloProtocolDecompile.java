// Static analysis of the authorized imported image only. Never attaches to a process.
// Run with the skill's analyzeHeadless launcher, -noanalysis, and a dedicated project.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashSet;

public class SoloProtocolDecompile extends GhidraScript {
    public void run() throws Exception {
        if ((getScriptArgs().length < 1 || getScriptArgs().length > 3) || currentProgram.getImageBase().getOffset() != 0x140000000L
            || !currentProgram.getName().equals("EliteDangerous64.exe"))
            throw new IllegalArgumentException("Expected the dedicated pinned x64 Elite image and one output directory.");
        Path output = Path.of(getScriptArgs()[0]).toAbsolutePath();
        Files.createDirectories(output);
        // Batch the entire currently identified Solo activity chain. These are
        // offline code locations; no game process or native function is run.
        long[] targets = {
            0x1D014A0L,0x1D05ED0L,0x1D82AE0L,0x1D653E0L,0x1D647D0L,0x1D64D40L,
            0x1D5F7A0L,0x1D1F170L,0x1D99DA0L,0x1D7D2A0L,0x1D82470L,0x1D4AC10L,0x1D18000L,0x2572160L,
            0x25EF7A0L,0x25EF950L,0x26160A0L,0x25E64F0L,
            0x2403660L,0x2407760L,0x2404620L,0x23FE9C0L,
            0x1D028A0L,0x1D6FCA0L,0x1D6FF60L,0x1D74440L,0x1D74950L,
            0x25FE850L,0x260D130L,0x1C09BD0L,0x13ABA80L,0x1350AE0L,0x33FDB40L,0x25BB540L,
            0x2566200L,0x1CFD440L,0x1D47080L,0x1D56380L,0x1D51890L
        };
        if (getScriptArgs().length >= 2) {
            var selected = new LinkedHashSet<Long>();
            if (getScriptArgs().length == 3 && !getScriptArgs()[2].equals("selected-only"))
                throw new IllegalArgumentException("Unknown static selection mode.");
            if (getScriptArgs().length == 2) for (long value : targets) selected.add(value);
            var rows = Files.readAllLines(Path.of(getScriptArgs()[1]), StandardCharsets.UTF_8);
            if (rows.size() > 256) throw new IllegalArgumentException("Static target manifest exceeds its bound.");
            for (String row : rows) {
                if (!row.matches("0x[0-9a-fA-F]{6,8}")) throw new IllegalArgumentException("Expected a fixed PE RVA.");
                long value = Long.parseLong(row.substring(2), 16);
                var block = currentProgram.getMemory().getBlock(currentProgram.getImageBase().add(value));
                if (value < 0x1000 || value >= 0x5000000 || block == null || !block.isExecute())
                    throw new IllegalArgumentException("RVA outside the pinned executable code section.");
                selected.add(value);
            }
            targets = selected.stream().mapToLong(Long::longValue).toArray();
        }
        // Define all selected entries first so that the decompiler can separate their calls.
        for (long rva : targets) {
            Address address = currentProgram.getImageBase().add(rva);
            if (getFunctionAt(address) == null) { disassemble(address); createFunction(address, "solo_" + Long.toHexString(rva)); }
        }
        DecompInterface decompiler = new DecompInterface();
        try {
            if (!decompiler.openProgram(currentProgram)) throw new IllegalStateException(decompiler.getLastMessage());
            for (long rva : targets) {
                monitor.checkCancelled();
                Function function = getFunctionAt(currentProgram.getImageBase().add(rva));
                if (function == null) throw new IllegalStateException("No exact function at " + Long.toHexString(rva));
                DecompileResults result = decompiler.decompileFunction(function, 45, monitor);
                if (!result.decompileCompleted()) throw new IllegalStateException(result.getErrorMessage());
                Path file = output.resolve("solo-" + Long.toHexString(rva) + ".c");
                Files.writeString(file, result.getDecompiledFunction().getC(), StandardCharsets.UTF_8);
                println("Decompiled fixed RVA " + Long.toHexString(rva));
            }
        } finally { decompiler.dispose(); }
    }
}
