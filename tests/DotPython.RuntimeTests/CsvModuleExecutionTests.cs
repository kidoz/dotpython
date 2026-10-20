using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CsvModuleExecutionTests
{
    /// <summary>
    /// The reader and the writer over CPython's own cases: quoting, escaping, embedded
    /// newlines, strict-mode refusals, line numbers, every quoting mode, the special
    /// single-empty-field records, dialect overrides and the argument-parser wording.
    /// </summary>
    [Fact]
    public void TheReaderAndWriterMatchPython()
    {
        var output = Run(
            """"
            import csv
            def try_(label, fn):
                try:
                    print(label, "ok", repr(fn()))
                except Exception as e:
                    print(label, type(e).__name__, e)
            class W:
                def __init__(self): self.parts = []
                def write(self, s): self.parts.append(s); return len(s)
            def wrow(row, **kw):
                out = W(); w = csv.writer(out, **kw)
                try: w.writerow(row)
                except Exception as e: return type(e).__name__ + ": " + str(e)
                return out.parts
            def rd(lines, **kw):
                out = []
                r = csv.reader(lines, **kw)
                try:
                    while True:
                        try: out.append(next(r))
                        except StopIteration: break
                except Exception as e:
                    out.append(type(e).__name__ + ": " + str(e))
                return out, r.line_num
            print("consts", csv.QUOTE_MINIMAL, csv.QUOTE_ALL, csv.QUOTE_NONNUMERIC, csv.QUOTE_NONE, csv.QUOTE_STRINGS, csv.QUOTE_NOTNULL, csv.__version__)
            print("types", type(csv.Error).__name__, csv.Error.__name__, csv.Error.__module__, issubclass(csv.Error, Exception))
            try_("reader-int", lambda: csv.reader(5))
            r = csv.reader(["a,b", "1,2"])
            print("reader", type(r).__name__, type(r).__module__, sorted(n for n in dir(r) if not n.startswith('_')), r.line_num)
            print("rows", list(r), r.line_num)
            print("rows2", rd(['a,"b,c"\r\n', '"x\ny",z\r\n']))
            print("rows3", rd(['a,"b\n', 'c",d\n']))
            print("rows4", rd(['"a', 'b",c']))
            print("rows5", rd(['a,b\nc,d\n']))
            print("rows6", rd(['a,"b\n'], strict=True))
            print("rows7", rd(['a,"b"c\n'], strict=True))
            print("rows8", rd(['a,"b"c\n']))
            print("rows9", rd(['a,b', 5]))
            print("rows10", rd(['a\n\n']))
            print("rows11", rd(['']))
            print("rows12", rd([' a, "b"\n'], skipinitialspace=True))
            print("rows13", rd(['a\\,b,c\n'], escapechar="\\"))
            print("rows14", rd(['"a""b"\n']))
            print("rows15", rd(['"a""b"\n'], doublequote=False))
            print("rows16", rd(['"a"b"\n'], doublequote=False))
            print("rows17", rd(['"a,b",c\n'], quoting=csv.QUOTE_NONE))
            print("rows18", rd(['"a",1,2.5,,nan\n'], quoting=csv.QUOTE_NONNUMERIC))
            print("rows19", rd(['a,"b",1\n'], quoting=csv.QUOTE_NOTNULL))
            print("rows20", rd(['a,"b",1\n'], quoting=csv.QUOTE_STRINGS))
            out = W(); w = csv.writer(out)
            print("writer", type(w).__name__, type(w).__module__, sorted(n for n in dir(w) if not n.startswith('_')))
            print("writerow", w.writerow(["a", "b"]), out.parts)
            try_("writer-int", lambda: csv.writer(5))
            try_("writerow-int", lambda: w.writerow(5))
            try_("writerow-none", lambda: w.writerow(None))
            print("w-types", wrow([None, 1, 1.5, True, "x,y", 'q"z', "a\nb"]))
            print("w-all", wrow(["a", 1, None, 2.5], quoting=csv.QUOTE_ALL))
            print("w-nonnum", wrow(["a", 1, None, 2.5, True], quoting=csv.QUOTE_NONNUMERIC))
            print("w-strings", wrow(["a", 1, None, 2.5, True], quoting=csv.QUOTE_STRINGS))
            print("w-notnull", wrow(["a", 1, None, 2.5], quoting=csv.QUOTE_NOTNULL))
            print("w-none", wrow(["a,b", "c"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            try_("w-none-err", lambda: wrow(["a,b", "c"], quoting=csv.QUOTE_NONE))
            print("w-special", wrow([]), wrow([""]), wrow([None]), wrow(["", ""]))
            print("w-esc", wrow(["a\\b"], escapechar="\\"), wrow(['a"b'], escapechar="\\"), wrow(["a\\b"], escapechar="\\", doublequote=False))
            print("w-dqfalse", wrow(['a"b'], doublequote=False), wrow(['a"b'], doublequote=False, escapechar="\\"))
            print("w-term", wrow(["a"], lineterminator="\n"), wrow(["a"], lineterminator=""))
            print("w-floats", wrow([1.0, float("inf"), float("-inf"), float("nan"), 1e20, 1e-7]))
            print("w-args", wrow("ab"), wrow(("a", "b")), wrow({"a": 1, "b": 2}))
            print("dialects", csv.list_dialects())
            d = csv.get_dialect("excel")
            print("dialect", type(d).__name__, type(d).__module__, d.delimiter, d.quotechar, d.doublequote, d.skipinitialspace, repr(d.lineterminator), d.quoting, d.escapechar, d.strict)
            print("excel", type(csv.excel).__name__, csv.excel.delimiter, csv.excel_tab.delimiter, csv.unix_dialect.quoting, repr(csv.unix_dialect.lineterminator))
            try_("get-missing", lambda: csv.get_dialect("nope"))
            try_("unreg-missing", lambda: csv.unregister_dialect("nope"))
            csv.register_dialect("mine", delimiter=";", lineterminator="\n")
            print("mine", csv.list_dialects(), csv.get_dialect("mine").delimiter, repr(csv.get_dialect("mine").lineterminator))
            print("mine-w", wrow(["a;b", "c"], dialect="mine"))
            try_("badfmt", lambda: csv.writer(W(), nope=1))
            try_("badquote", lambda: csv.writer(W(), quoting=99))
            try_("longdelim", lambda: csv.writer(W(), delimiter=";;"))
            try_("empty-delim", lambda: csv.writer(W(), delimiter=""))
            try_("newline-delim", lambda: csv.writer(W(), delimiter="\n"))
            try_("lineterm-int", lambda: csv.writer(W(), lineterminator=5))
            try_("escape-long", lambda: csv.writer(W(), escapechar="\\\\"))
            try_("dq-int", lambda: wrow(["a"], doublequote=1))
            try_("dup", lambda: csv.writer(W(), "excel", dialect="excel"))
            try_("reader-none", lambda: csv.reader())
            try_("writer-3pos", lambda: csv.writer(W(), "excel", 1))
            print("dictreader", list(csv.DictReader(["a,b\n", "1,2\n", "3\n"])))
            r2 = csv.DictReader(["a,b\n", "1,2,3\n", "4\n"], restkey="R", restval="D")
            print("dict-rest", list(r2), r2.line_num)
            r3 = csv.DictReader(["1,2\n"], fieldnames=["x", "y"])
            print("dict-fields", list(r3), r3.fieldnames, r3.line_num)
            out = W(); dw = csv.DictWriter(out, fieldnames=["a", "b"])
            print("dw", dw.writeheader(), dw.writerow({"a": 1, "b": 2}), out.parts)
            out = W(); dw = csv.DictWriter(out, fieldnames=["a"], extrasaction="IGNORE"); dw.writerow({"a": 1, "z": 9}); print("dw-ignore", out.parts)
            try_("dw-extra", lambda: csv.DictWriter(W(), fieldnames=["a"]).writerow({"a": 1, "z": 9}))
            try_("dw-nofields", lambda: csv.DictWriter(W()))
            try_("extras-bad", lambda: csv.DictWriter(W(), fieldnames=[], extrasaction="nope"))
            print("sniff", [(x.delimiter, x.quotechar, x.doublequote, x.skipinitialspace, repr(x.lineterminator), x.quoting) for x in [csv.Sniffer().sniff("a,b\n1,2\n")]])
            print("sniff2", csv.Sniffer().sniff("a;b\n1;2\n").delimiter, csv.Sniffer().sniff("a\tb\n1\t2\n").delimiter, csv.Sniffer().sniff("a|b\n1|2\n").delimiter)
            print("sniff3", repr(csv.Sniffer().sniff("a b\n1 2\n").delimiter), csv.Sniffer().sniff("abc").delimiter)
            print("sniff4", [(k, repr(getattr(csv.Sniffer().sniff('a,"b,c"\n1,2\n'), k))) for k in ("delimiter", "quotechar", "doublequote", "skipinitialspace")])
            print("sniff5", [(k, repr(getattr(csv.Sniffer().sniff('a; b\n1; 2\n'), k))) for k in ("delimiter", "skipinitialspace")])
            print("sniff6", csv.Sniffer().sniff('a,"b""c"\n1,2\n').doublequote)
            print("sniff7", csv.Sniffer().sniff("a,b\n1,2\n", delimiters=[",", ";"]).delimiter)
            try_("sniff-bad", lambda: csv.Sniffer().sniff("a;b\n1;2", delimiters=","))
            print("hasheader", csv.Sniffer().has_header("a,b\n1,2\n3,4\n"), csv.Sniffer().has_header("1,2\n3,4\n5,6\n"))
            print("fieldlimit", csv.field_size_limit())
            print("fieldlimit2", csv.field_size_limit(10), csv.field_size_limit(), csv.field_size_limit(131072))
            try_("fieldlimit-bad", lambda: csv.field_size_limit("x"))
            csv.field_size_limit(10)
            print("limit-exact", rd(['a'*10 + "\n"]))
            print("limit-over", rd(['a'*11 + "\n"]))
            csv.field_size_limit(131072)
            print("classes", csv.Dialect().delimiter if False else "skip")
            try_("dialect-base", lambda: csv.Dialect())
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "consts 0 1 2 3 4 5 1.0",
                "types type Error _csv True",
                "reader-int TypeError 'int' object is not iterable",
                "reader reader _csv ['dialect', 'line_num'] 0",
                "rows [['a', 'b'], ['1', '2']] 2",
                "rows2 ([['a', 'b,c'], ['x\\ny', 'z']], 2)",
                "rows3 ([['a', 'b\\nc', 'd']], 2)",
                "rows4 ([['ab', 'c']], 2)",
                "rows5 ([\"Error: new-line character seen in unquoted field - do you need to open the file with newline=''?\"], 1)",
                "rows6 (['Error: unexpected end of data'], 1)",
                "rows7 (['Error: \\',\\' expected after \\'\"\\''], 1)",
                "rows8 ([['a', 'bc']], 1)",
                "rows9 ([['a', 'b'], 'Error: iterator should return strings, not int (the file should be opened in text mode)'], 1)",
                "rows10 ([['a']], 1)",
                "rows11 ([[]], 1)",
                "rows12 ([['a', 'b']], 1)",
                "rows13 ([['a,b', 'c']], 1)",
                "rows14 ([['a\"b']], 1)",
                "rows15 ([['a\"b\"']], 1)",
                "rows16 ([['ab\"']], 1)",
                "rows17 ([['\"a', 'b\"', 'c']], 1)",
                "rows18 ([['a', 1.0, 2.5, '', nan]], 1)",
                "rows19 ([['a', 'b', '1']], 1)",
                "rows20 ([\"ValueError: could not convert string to float: 'a'\"], 1)",
                "writer writer _csv ['dialect', 'writerow', 'writerows']",
                "writerow 5 ['a,b\\r\\n']",
                "writer-int TypeError argument 1 must have a \"write\" method",
                "writerow-int Error iterable expected, not int",
                "writerow-none Error iterable expected, not NoneType",
                "w-types [',1,1.5,True,\"x,y\",\"q\"\"z\",\"a\\nb\"\\r\\n']",
                "w-all ['\"a\",\"1\",\"\",\"2.5\"\\r\\n']",
                "w-nonnum ['\"a\",1,\"\",2.5,True\\r\\n']",
                "w-strings ['\"a\",1,,2.5,True\\r\\n']",
                "w-notnull ['\"a\",\"1\",,\"2.5\"\\r\\n']",
                "w-none ['a\\\\,b,c\\r\\n']",
                "w-none-err ok 'Error: need to escape, but no escapechar set'",
                "w-special ['\\r\\n'] ['\"\"\\r\\n'] ['\"\"\\r\\n'] [',\\r\\n']",
                "w-esc ['a\\\\\\\\b\\r\\n'] ['\"a\"\"b\"\\r\\n'] ['a\\\\\\\\b\\r\\n']",
                "w-dqfalse Error: need to escape, but no escapechar set ['a\\\\\"b\\r\\n']",
                "w-term ['a\\n'] ['a']",
                "w-floats ['1.0,inf,-inf,nan,1e+20,1e-07\\r\\n']",
                "w-args ['a,b\\r\\n'] ['a,b\\r\\n'] ['a,b\\r\\n']",
                "dialects ['excel', 'excel-tab', 'unix']",
                "dialect Dialect _csv , \" True False '\\r\\n' 0 None False",
                "excel type , 	 1 '\\n'",
                "get-missing Error unknown dialect",
                "unreg-missing Error unknown dialect",
                "mine ['excel', 'excel-tab', 'unix', 'mine'] ; '\\n'",
                "mine-w ['\"a;b\";c\\n']",
                "badfmt TypeError this function got an unexpected keyword argument 'nope'",
                "badquote TypeError bad \"quoting\" value",
                "longdelim TypeError \"delimiter\" must be a unicode character, not a string of length 2",
                "empty-delim TypeError \"delimiter\" must be a unicode character, not a string of length 0",
                "newline-delim ValueError bad delimiter value",
                "lineterm-int TypeError \"lineterminator\" must be a string, not int",
                "escape-long TypeError \"escapechar\" must be a unicode character or None, not a string of length 2",
                "dq-int ok ['a\\r\\n']",
                "dup TypeError argument for function given by name ('dialect') and position (1)",
                "reader-none TypeError reader expected at least 1 argument, got 0",
                "writer-3pos TypeError writer expected at most 2 arguments, got 3",
                "dictreader [{'a': '1', 'b': '2'}, {'a': '3', 'b': None}]",
                "dict-rest [{'a': '1', 'b': '2', 'R': ['3']}, {'a': '4', 'b': 'D'}] 3",
                "dict-fields [{'x': '1', 'y': '2'}] ['x', 'y'] 1",
                "dw 5 5 ['a,b\\r\\n', '1,2\\r\\n']",
                "dw-ignore ['1\\r\\n']",
                "dw-extra ValueError dict contains fields not in fieldnames: 'z'",
                "dw-nofields TypeError DictWriter.__init__() missing 1 required positional argument: 'fieldnames'",
                "extras-bad ValueError extrasaction (nope) must be 'raise' or 'ignore'",
                "sniff [(',', '\"', False, False, \"'\\\\r\\\\n'\", 0)]",
                "sniff2 ; 	 |",
                "sniff3 ' ' c",
                "sniff4 [('delimiter', \"','\"), ('quotechar', '\\'\"\\''), ('doublequote', 'False'), ('skipinitialspace', 'False')]",
                "sniff5 [('delimiter', \"';'\"), ('skipinitialspace', 'True')]",
                "sniff6 True",
                "sniff7 ,",
                "sniff-bad Error Could not determine delimiter",
                "hasheader True False",
                "fieldlimit 131072",
                "fieldlimit2 131072 10 10",
                "fieldlimit-bad TypeError limit must be an integer",
                "limit-exact ([['aaaaaaaaaa']], 1)",
                "limit-over (['Error: field larger than field limit (10)'], 1)",
                "classes skip",
                "dialect-base Error \"delimiter\" must be a unicode character, not NoneType",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The dialect registry and its built-in dialects, registration and removal,
    /// validation, `DictReader`/`DictWriter` field mapping and `Sniffer`'s public and
    /// private detection helpers.
    /// </summary>
    [Fact]
    public void DialectsDictHelpersAndSnifferMatchPython()
    {
        var output = Run(
            """"
            import csv
            def try_(label, fn):
                try:
                    print(label, "ok", repr(fn()))
                except Exception as e:
                    print(label, type(e).__name__, e)
            class W:
                def __init__(self): self.parts = []
                def write(self, s): self.parts.append(s); return len(s)
            def wrow(row, **kw):
                try:
                    out = W(); w = csv.writer(out, **kw); w.writerow(row)
                except Exception as e: return type(e).__name__ + ": " + str(e)
                return out.parts
            def rd(lines, **kw):
                out = []
                r = csv.reader(lines, **kw)
                try:
                    while True:
                        try: out.append(next(r))
                        except StopIteration: break
                except Exception as e:
                    out.append(type(e).__name__ + ": " + str(e))
                return out, r.line_num
            print("w-all", wrow(["a", 1, None, 2.5], quoting=csv.QUOTE_ALL))
            print("w-none-delim", wrow(["a,b"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-none-newline", wrow(["a\nb"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-none-cr", wrow(["a\rb"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-none-quote", wrow(['a"b'], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-none-esc", wrow(["a\\b"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-none-empty", wrow([""], quoting=csv.QUOTE_NONE))
            print("w-none-single-quote", wrow(['"'], quoting=csv.QUOTE_NONE))
            print("w-qc-none", wrow(["a"], quoting=csv.QUOTE_NONE, quotechar=None))
            print("w-qc-none-min", wrow(["a"], quotechar=None))
            print("w-qc-none-all", wrow(["a"], quotechar=None, quoting=csv.QUOTE_ALL))
            print("w-delim-quote", wrow(["a"], delimiter='"' , quotechar='"'))
            print("w-delim-quote-none", wrow(["a"], delimiter='"', quotechar='"', quoting=csv.QUOTE_NONE))
            print("w-esc-delim", wrow(["a"], delimiter=';', escapechar=";"))
            print("w-esc-quote", wrow(["a"], quotechar='"', escapechar='"'))
            print("w-multi", wrow(["a\r\nb"], escapechar="\\"))
            print("w-none-multi", wrow(["a\r\nb"], escapechar="\\", quoting=csv.QUOTE_NONE))
            print("w-crlf-field", wrow(["a\r\nb"]))
            print("w-leading-space", wrow([" a", " b"]))
            print("w-skip", wrow([" a", " b"], skipinitialspace=True))
            try_("w-delim-multi", lambda: csv.writer(W(), delimiter="ab"))
            try_("w-quote-none-str", lambda: csv.writer(W(), quotechar=5))
            try_("w-escape-none-str", lambda: csv.writer(W(), escapechar=5))
            try_("w-lineterm-none", lambda: csv.writer(W(), lineterminator=None))
            try_("w-arity0", lambda: csv.writer())
            try_("w-arity3", lambda: csv.writer(W(), "excel", 1))
            try_("w-kwfile", lambda: csv.writer(W(), f=W()))
            try_("w-dup-dialect", lambda: csv.writer(W(), "excel", dialect="excel"))
            try_("w-unknownfmt", lambda: csv.writer(W(), nope=1))
            try_("r-arity0", lambda: csv.reader())
            try_("r-arity3", lambda: csv.reader([], "excel", 1))
            try_("r-kwfile", lambda: csv.reader(f=[]))
            try_("r-dup-dialect", lambda: csv.reader([], "excel", dialect="excel"))
            try_("r-unknownfmt", lambda: csv.reader([], nope=1))
            try_("r-quote-bad", lambda: csv.reader([], quoting=9))
            print("r-quote-float", try_("r-quote-float", lambda: csv.reader([], quoting=1.5)))
            print("r-attr", csv.reader(["a;b\n"], delimiter=";").dialect.delimiter)
            print("r-line-num-type", type(csv.reader([]).line_num).__name__)
            try_("r-set-line", lambda: setattr(csv.reader([]), "line_num", 5))
            try_("r-set-dialect", lambda: setattr(csv.reader([]), "dialect", 5))
            try_("w-set-dialect", lambda: setattr(csv.writer(W()), "dialect", 5))
            class Mine(csv.Dialect):
                delimiter = ";"
                quotechar = "'"
                doublequote = False
                skipinitialspace = True
                escapechar = "\\"
                lineterminator = "\n"
                quoting = csv.QUOTE_MINIMAL
                strict = True
            csv.register_dialect("mine2", Mine)
            print("mine2", csv.get_dialect("mine2").delimiter, csv.get_dialect("mine2").quotechar, csv.get_dialect("mine2").doublequote, csv.get_dialect("mine2").lineterminator)
            print("mine2-w", wrow(["a;b", "c'd"], dialect="mine2"))
            print("mine-class-w", wrow(["a;b", "c'd"], dialect=Mine))
            print("mine-inst-w", wrow(["a;b", "c'd"], dialect=Mine()))
            print("mine-override", wrow(["a;b"], dialect=Mine, delimiter="|"))
            print("reader-class", rd(["a;b\n"], dialect=Mine))
            try_("reg-dupe", lambda: csv.register_dialect("mine2", Mine))
            try_("reg-name-int", lambda: csv.register_dialect(5, delimiter=";"))
            try_("reg-name-none", lambda: csv.register_dialect(None, delimiter=";"))
            try_("reg-none-spec", lambda: csv.register_dialect("z", None))
            print("reg-none-get", csv.get_dialect("z").delimiter)
            try_("reg-object", lambda: csv.register_dialect("z2", object()))
            try_("reg-int", lambda: csv.register_dialect("z3", 5))
            try_("reg-badfmt", lambda: csv.register_dialect("z4", delimiter="ab"))
            print("reg-kw", csv.register_dialect("z5", delimiter="|", quoting=csv.QUOTE_ALL))
            print("z5", csv.get_dialect("z5").delimiter, csv.get_dialect("z5").quoting)
            print("z5-w", wrow(["a", "b"], dialect="z5"))
            csv.unregister_dialect("z5")
            try_("get-z5", lambda: csv.get_dialect("z5"))
            try_("unreg-z5", lambda: csv.unregister_dialect("z5"))
            print("list-order", csv.list_dialects())
            print("dialect-instance", type(csv.get_dialect("excel")).__name__, type(csv.get_dialect("excel")).__module__)
            print("excel-class", type(csv.excel).__name__, csv.excel.__module__)
            print("dialect-base-attrs", csv.Dialect.delimiter, csv.Dialect.quotechar, csv.Dialect.quoting)
            try_("dialect-call", lambda: csv.Dialect())
            try_("dialect-call-arg", lambda: csv.Dialect(csv.excel))
            print("dict-reader-skip", list(csv.DictReader(["a,b\n", "\n", "1,2\n"])))
            r = csv.DictReader(["a,b\n", "1,2\n"])
            print("dr-iter", r is iter(r), r.line_num)
            print("dr-first", next(r), r.line_num, r.fieldnames)
            print("dr-again", next(r, "done"), r.line_num)
            r2 = csv.DictReader([], fieldnames=["a", "b"])
            print("dr-empty", list(r2), r2.fieldnames, r2.line_num)
            r3 = csv.DictReader(["1,2\n"])
            print("dr-fields-lazy", r3.line_num, r3.fieldnames, r3.line_num)
            r4 = csv.DictReader(["a,b\n", "1\n", "1,2,3\n"], restval="D", restkey="R")
            print("dr-rest", list(r4))
            r5 = csv.DictReader(["a,b\n", "1,2\n"])
            r5.fieldnames = ["x", "y"]
            print("dr-setfields", list(r5))
            try_("dr-extra", lambda: csv.DictReader([], nope=1))
            try_("dr-arity", lambda: csv.DictReader())
            print("dr-dialect", list(csv.DictReader(["a;b\n"], delimiter=";")))
            print("dr-nonnum", list(csv.DictReader(['"a",1\n'], quoting=csv.QUOTE_NONNUMERIC)))
            out = W(); dw = csv.DictWriter(out, fieldnames=["a", "b"])
            print("dw-writes", dw.writerows([{"a": 1, "b": 2}, {"a": 3}]), out.parts)
            out = W(); dw = csv.DictWriter(out, fieldnames=["a", "b"], restval="R")
            print("dw-header", dw.writeheader(), out.parts)
            out = W(); dw = csv.DictWriter(out, fieldnames=["a"], extrasaction="ignore")
            print("dw-ignore", dw.writerow({"a": 1, "z": 2}), out.parts)
            try_("dw-extra", lambda: csv.DictWriter(W(), fieldnames=["a"]).writerow({"a": 1, "z": 2}))
            try_("dw-missing", lambda: csv.DictWriter(W()))
            try_("dw-extras", lambda: csv.DictWriter(W(), fieldnames=[], extrasaction="nope"))
            try_("dw-extras-int", lambda: csv.DictWriter(W(), fieldnames=[], extrasaction=5))
            try_("dw-unknown", lambda: csv.DictWriter(W(), fieldnames=[], nope=1))
            print("dw-dialect", (lambda o: (csv.DictWriter(o, fieldnames=["a"], delimiter=";").writerow({"a": 1}), o.parts))(W()))
            print("consts-type", [type(x).__name__ for x in (csv.QUOTE_MINIMAL, csv.QUOTE_ALL, csv.__version__)])
            print("sniff-semicolon-space", [(k, repr(getattr(csv.Sniffer().sniff("a; b;c\n1; 2;3\n"), k))) for k in ("delimiter", "skipinitialspace")])
            try_("sniff-single-col", lambda: csv.Sniffer().sniff("abc\ndef\n").delimiter)
            print("sniff-single-col-quoted", [(k, repr(getattr(csv.Sniffer().sniff('"abc"\n"def"\n'), k))) for k in ("delimiter", "quotechar", "doublequote", "skipinitialspace")])
            print("sniff-pipe-space", csv.Sniffer().sniff("a| b\n1| 2\n").delimiter, csv.Sniffer().sniff("a| b\n1| 2\n").skipinitialspace)
            print("sniff-delim-colon", csv.Sniffer().sniff("a:b:c\n1:2:3\n").delimiter)
            print("sniff-preferred", csv.Sniffer().sniff("a,b;c\n1,2;3\n").delimiter)
            print("sniff-quoted-comma", [(k, repr(getattr(csv.Sniffer().sniff('a,"b,c"\n1,2\n'), k))) for k in ("delimiter", "quotechar", "doublequote", "skipinitialspace")])
            print("sniff-quoted-semicolon", csv.Sniffer().sniff("a;'b;c'\n1;2\n").delimiter)
            print("sniff-hasheader1", csv.Sniffer().has_header("a,b\n1,2\n3,4\n"))
            print("sniff-hasheader2", csv.Sniffer().has_header("1,2\n3,4\n5,6\n"))
            print("sniff-hasheader3", csv.Sniffer().has_header("name,age\nAlice,30\nBob,40\n"))
            print("sniff-hasheader4", csv.Sniffer().has_header("name,age\nAlice,30\nBob,40\nCarl,50\nDina,60\n"))
            print("sniff-hasheader5", csv.Sniffer().has_header("1,2\n3,4\n"))
            try_("sniff-hasheader6", lambda: csv.Sniffer().has_header("a,b\n1,2\n3\n"))
            try_("sniff-hasheader7", lambda: csv.Sniffer().has_header("a\nb\nc\n"))
            try_("sniff-missing", lambda: csv.Sniffer().sniff())
            try_("sniff-extra", lambda: csv.Sniffer().sniff("a", None, 1))
            try_("sniff-int", lambda: csv.Sniffer().sniff(5))
            try_("hh-missing", lambda: csv.Sniffer().has_header())
            try_("hh-extra", lambda: csv.Sniffer().has_header("a", 1))
            print("sniffer-underscore", hasattr(csv.Sniffer(), "_guess_delimiter"), hasattr(csv.Sniffer(), "_guess_quote_and_delimiter"))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "w-all ['\"a\",\"1\",\"\",\"2.5\"\\r\\n']",
                "w-none-delim ['a\\\\,b\\r\\n']",
                "w-none-newline ['a\\\\\\nb\\r\\n']",
                "w-none-cr ['a\\\\\\rb\\r\\n']",
                "w-none-quote ['a\\\\\"b\\r\\n']",
                "w-none-esc ['a\\\\\\\\b\\r\\n']",
                "w-none-empty Error: single empty field record must be quoted",
                "w-none-single-quote Error: need to escape, but no escapechar set",
                "w-qc-none ['a\\r\\n']",
                "w-qc-none-min ['a\\r\\n']",
                "w-qc-none-all TypeError: quotechar must be set if quoting enabled",
                "w-delim-quote ValueError: bad delimiter or quotechar value",
                "w-delim-quote-none ValueError: bad delimiter or quotechar value",
                "w-esc-delim ValueError: bad delimiter or escapechar value",
                "w-esc-quote ValueError: bad escapechar or quotechar value",
                "w-multi ['\"a\\r\\nb\"\\r\\n']",
                "w-none-multi ['a\\\\\\r\\\\\\nb\\r\\n']",
                "w-crlf-field ['\"a\\r\\nb\"\\r\\n']",
                "w-leading-space [' a, b\\r\\n']",
                "w-skip [' a, b\\r\\n']",
                "w-delim-multi TypeError \"delimiter\" must be a unicode character, not a string of length 2",
                "w-quote-none-str TypeError \"quotechar\" must be a unicode character or None, not int",
                "w-escape-none-str TypeError \"escapechar\" must be a unicode character or None, not int",
                "w-lineterm-none TypeError \"lineterminator\" must be a string, not NoneType",
                "w-arity0 TypeError writer expected at least 1 argument, got 0",
                "w-arity3 TypeError writer expected at most 2 arguments, got 3",
                "w-kwfile TypeError this function got an unexpected keyword argument 'f'",
                "w-dup-dialect TypeError argument for function given by name ('dialect') and position (1)",
                "w-unknownfmt TypeError this function got an unexpected keyword argument 'nope'",
                "r-arity0 TypeError reader expected at least 1 argument, got 0",
                "r-arity3 TypeError reader expected at most 2 arguments, got 3",
                "r-kwfile TypeError reader expected at least 1 argument, got 0",
                "r-dup-dialect TypeError argument for function given by name ('dialect') and position (1)",
                "r-unknownfmt TypeError this function got an unexpected keyword argument 'nope'",
                "r-quote-bad TypeError bad \"quoting\" value",
                "r-quote-float TypeError \"quoting\" must be an integer, not float",
                "r-quote-float None",
                "r-attr ;",
                "r-line-num-type int",
                "r-set-line AttributeError readonly attribute",
                "r-set-dialect AttributeError readonly attribute",
                "w-set-dialect AttributeError readonly attribute",
                "mine2 ; ' False ",
                "",
                "mine2-w [\"'a;b';c\\\\'d\\n\"]",
                "mine-class-w [\"'a;b';c\\\\'d\\n\"]",
                "mine-inst-w [\"'a;b';c\\\\'d\\n\"]",
                "mine-override ['a;b\\n']",
                "reader-class ([['a', 'b']], 1)",
                "reg-dupe ok None",
                "reg-name-int TypeError dialect name must be a string",
                "reg-name-none TypeError dialect name must be a string",
                "reg-none-spec ok None",
                "reg-none-get ,",
                "reg-object ok None",
                "reg-int ok None",
                "reg-badfmt TypeError \"delimiter\" must be a unicode character, not a string of length 2",
                "reg-kw None",
                "z5 | 1",
                "z5-w ['\"a\"|\"b\"\\r\\n']",
                "get-z5 Error unknown dialect",
                "unreg-z5 Error unknown dialect",
                "list-order ['excel', 'excel-tab', 'unix', 'mine2', 'z', 'z2', 'z3']",
                "dialect-instance Dialect _csv",
                "excel-class type csv",
                "dialect-base-attrs None None None",
                "dialect-call Error \"delimiter\" must be a unicode character, not NoneType",
                "dialect-call-arg TypeError Dialect.__init__() takes 1 positional argument but 2 were given",
                "dict-reader-skip [{'a': '1', 'b': '2'}]",
                "dr-iter True 0",
                "dr-first {'a': '1', 'b': '2'} 2 ['a', 'b']",
                "dr-again done 2",
                "dr-empty [] ['a', 'b'] 0",
                "dr-fields-lazy 0 ['1', '2'] 1",
                "dr-rest [{'a': '1', 'b': 'D'}, {'a': '1', 'b': '2', 'R': ['3']}]",
                "dr-setfields [{'x': 'a', 'y': 'b'}, {'x': '1', 'y': '2'}]",
                "dr-extra TypeError this function got an unexpected keyword argument 'nope'",
                "dr-arity TypeError DictReader.__init__() missing 1 required positional argument: 'f'",
                "dr-dialect []",
                "dr-nonnum []",
                "dw-writes None ['1,2\\r\\n', '3,\\r\\n']",
                "dw-header 5 ['a,b\\r\\n']",
                "dw-ignore 3 ['1\\r\\n']",
                "dw-extra ValueError dict contains fields not in fieldnames: 'z'",
                "dw-missing TypeError DictWriter.__init__() missing 1 required positional argument: 'fieldnames'",
                "dw-extras ValueError extrasaction (nope) must be 'raise' or 'ignore'",
                "dw-extras-int AttributeError 'int' object has no attribute 'lower'",
                "dw-unknown TypeError this function got an unexpected keyword argument 'nope'",
                "dw-dialect (3, ['1\\r\\n'])",
                "consts-type ['int', 'int', 'str']",
                "sniff-semicolon-space [('delimiter', \"';'\"), ('skipinitialspace', 'False')]",
                "sniff-single-col Error Could not determine delimiter",
                "sniff-single-col-quoted [('delimiter', '\\'\"\\''), ('quotechar', '\\'\"\\''), ('doublequote', 'False'), ('skipinitialspace', 'False')]",
                "sniff-pipe-space   False",
                "sniff-delim-colon :",
                "sniff-preferred ,",
                "sniff-quoted-comma [('delimiter', \"','\"), ('quotechar', '\\'\"\\''), ('doublequote', 'False'), ('skipinitialspace', 'False')]",
                "sniff-quoted-semicolon ;",
                "sniff-hasheader1 True",
                "sniff-hasheader2 False",
                "sniff-hasheader3 True",
                "sniff-hasheader4 True",
                "sniff-hasheader5 False",
                "sniff-hasheader6 Error Could not determine delimiter",
                "sniff-hasheader7 Error Could not determine delimiter",
                "sniff-missing TypeError Sniffer.sniff() missing 1 required positional argument: 'sample'",
                "sniff-extra TypeError Sniffer.sniff() takes from 2 to 3 positional arguments but 4 were given",
                "sniff-int AttributeError 'int' object has no attribute 'replace'",
                "hh-missing TypeError Sniffer.has_header() missing 1 required positional argument: 'sample'",
                "hh-extra TypeError Sniffer.has_header() takes 2 positional arguments but 3 were given",
                "sniffer-underscore True True",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The reader and writer edge cases: escaped line continuations, byte-for-byte
    /// quoting of None and empty fields, custom `__str__` objects, CRLF boundaries and
    /// the `Sniffer` heuristics over irregular samples.
    /// </summary>
    [Fact]
    public void TheCsvEdgeCasesMatchPython()
    {
        var output = Run(
            """"
            import csv
            def try_(label, fn):
                try:
                    print(label, "ok", repr(fn()))
                except Exception as e:
                    print(label, type(e).__name__, e)
            class W:
                def __init__(self): self.parts = []
                def write(self, s): self.parts.append(s); return len(s)
            def wrow(row, **kw):
                try:
                    out = W(); w = csv.writer(out, **kw); w.writerow(row)
                except Exception as e: return type(e).__name__ + ": " + str(e)
                return out.parts
            def rd(lines, **kw):
                out = []
                r = csv.reader(lines, **kw)
                try:
                    while True:
                        try: out.append(next(r))
                        except StopIteration: break
                except Exception as e:
                    out.append(type(e).__name__ + ": " + str(e))
                return out, r.line_num
            class S:
                def __init__(self, text): self.text = text
                def __str__(self): return self.text
            print("p3-1", rd(['a\r', '\n']))
            print("p3-2", rd(['a\\', 'b\n'], escapechar="\\"))
            print("p3-3", rd(['a"b,c\n'], quoting=csv.QUOTE_NONE))
            print("p3-4", rd(['a"b,c\n'], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("p3-5", rd(['"a"  ,b\n'], skipinitialspace=True))
            print("p3-6", rd(['"a" b,c\n'], skipinitialspace=True))
            print("p3-7", rd(['a, "b,c"\n'], skipinitialspace=True))
            print("p3-8", rd(['a\tb\n'], delimiter="\t"))
            print("p3-9", rd(['a;b\n'], delimiter=";", quoting=csv.QUOTE_NONNUMERIC))
            print("p3-10", rd([' 1 ,x\n'], quoting=csv.QUOTE_NONNUMERIC))
            print("p3-11", rd(['1_0,2\n'], quoting=csv.QUOTE_NONNUMERIC))
            print("p3-12", rd(['1e3,-0.0,+2,.5\n'], quoting=csv.QUOTE_NONNUMERIC))
            print("p3-13", rd(['"a"\r\n'], quoting=csv.QUOTE_NONE))
            print("p3-14", rd(['"a\n'], quoting=csv.QUOTE_NONE))
            print("p3-15", rd(['a,b\rc\n']))
            print("p3-16", rd(['a,b\r', 'c\n']))
            print("p3-17", rd(['a,b\r\n']))
            print("p3-18", rd(['a,b\n'], lineterminator="X"))
            print("p3-19", rd(['a,b'], quoting=csv.QUOTE_STRINGS))
            print("p3-20", rd(['a,b\n'], quoting=csv.QUOTE_STRINGS))
            print("w-obj", wrow([S("x,y")]))
            print("w-obj-none", wrow([S("")], quoting=csv.QUOTE_STRINGS))
            print("w-nonnum-obj", wrow([S("3")], quoting=csv.QUOTE_NONNUMERIC))
            print("w-notnull-obj", wrow([S(""), None], quoting=csv.QUOTE_NOTNULL))
            print("w-str-empty-nonnull", wrow([""], quoting=csv.QUOTE_NOTNULL))
            print("w-nonnum-single-empty", wrow([""], quoting=csv.QUOTE_NONNUMERIC))
            print("w-none-single-none-esc", wrow([None], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-floats2", wrow([0.0, -0.0, 1.5, float("inf")]))
            print("w-bool", wrow([True, False], quoting=csv.QUOTE_NONNUMERIC))
            print("w-nested", wrow([["a"], ("b",)]))
            print("w-gen", wrow((c for c in "ab")))
            print("w-dict", wrow({"a": 1, "b": 2}))
            print("w-esc-both", wrow(["a,b"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-esc-quote-none", wrow(["a,b"], quoting=csv.QUOTE_NONE, escapechar="\\", quotechar="'"))
            print("w-esc-quote-q", wrow(["a'b"], quoting=csv.QUOTE_NONE, escapechar="\\", quotechar="'"))
            print("w-esc-literal", wrow(["a\\b"], quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-q-notnull", wrow([None, "", 0, False], quoting=csv.QUOTE_NOTNULL))
            print("w-q-strings", wrow([None, "", 0, False, "s"], quoting=csv.QUOTE_STRINGS))
            print("w-q-nonnum", wrow([None, "", 0, False, "s"], quoting=csv.QUOTE_NONNUMERIC))
            print("w-q-all", wrow([None, "", 0], quoting=csv.QUOTE_ALL))
            print("w-space-delim", wrow(["a b", "c"], delimiter=" "))
            print("w-space-delim-q", wrow(["a b", "c"], delimiter=" ", quoting=csv.QUOTE_NONE, escapechar="\\"))
            print("w-qc", wrow(["a|b", "c"], quotechar="|"))
            print("w-qc-space", wrow(["a b"], quotechar=" "))
            print("w-term-cr", wrow(["a"], lineterminator="\r"))
            print("w-term-empty-field", wrow(["", ""], lineterminator=""))
            print("r-dialect-inst", rd(["a;b\n"], dialect=csv.get_dialect("excel"), delimiter=";")[0])
            try_("r-dialect-int", lambda: rd(["a,b\n"], dialect=5)[0])
            try_("w-dialect-int", lambda: wrow(["a"], dialect=5))
            try_("w-dialect-none", lambda: wrow(["a"], dialect=None))
            try_("r-dialect-none", lambda: rd(["a,b\n"], dialect=None)[0])
            print("reg-missing-attrs", csv.register_dialect("m3", type("Q", (), {"delimiter": ";"})))
            try_("m3-w", lambda: wrow(["a;b"], dialect="m3"))
            print("sniff-11lines", csv.Sniffer().sniff("\n".join(["a,b,c"]*11) + "\n").delimiter)
            print("sniff-12lines", csv.Sniffer().sniff("\n".join(["a;b;c"]*12) + "\n").delimiter)
            print("sniff-war", csv.Sniffer().sniff("a,b,c\n1,2,3\n4,5,6\n").delimiter)
            print("sniff-quoted-single", [(k, repr(getattr(csv.Sniffer().sniff('"a"\n"b"\n'), k))) for k in ("delimiter", "quotechar", "doublequote", "skipinitialspace")])
            try_("sniff-quoted-doubled", lambda: [(k, repr(getattr(csv.Sniffer().sniff('"a""b"\n"c"\n'), k))) for k in ("delimiter", "quotechar", "doublequote")])
            print("sniff-quoted-tab", csv.Sniffer().sniff('"a\tb"\tc\n').delimiter)
            print("sniff-apostrophe", csv.Sniffer().sniff("a,'b,c'\n1,2\n").delimiter)
            print("sniff-space-quote", csv.Sniffer().sniff('a, "b c"\n1, 2\n').delimiter)
            print("sniff-delims6", csv.Sniffer().sniff("a:b\n1:2\n", delimiters=":;").delimiter)
            print("sniff-delims7", csv.Sniffer().sniff("a:b\n1:2\n", delimiters=[":"]).delimiter)
            try_("hh-21", lambda: csv.Sniffer().has_header("h\n" + "\n".join(str(i) for i in range(25))))
            try_("hh-irregular", lambda: csv.Sniffer().has_header("a,b\n1,2\n3,4,5\n6,7\n"))
            print("guess-quote", csv.Sniffer()._guess_quote_and_delimiter('a,"b,c"\n1,2\n', None))
            print("guess-delim", csv.Sniffer()._guess_delimiter("a,b\n1,2\n", None))
            print("pref", csv.Sniffer().preferred)
            print("gq-none", csv.Sniffer()._guess_quote_and_delimiter("abc", None))
            print("gd-none", csv.Sniffer()._guess_delimiter("abc", None))
            try_("gq-missing", lambda: csv.Sniffer()._guess_quote_and_delimiter())
            try_("gd-missing", lambda: csv.Sniffer()._guess_delimiter("a,b\n"))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "p3-1 ([['a'], []], 2)",
                "p3-2 ([['a\\nb']], 2)",
                "p3-3 ([['a\"b', 'c']], 1)",
                "p3-4 ([['a\"b', 'c']], 1)",
                "p3-5 ([['a  ', 'b']], 1)",
                "p3-6 ([['a b', 'c']], 1)",
                "p3-7 ([['a', 'b,c']], 1)",
                "p3-8 ([['a', 'b']], 1)",
                "p3-9 ([\"ValueError: could not convert string to float: 'a'\"], 1)",
                "p3-10 ([\"ValueError: could not convert string to float: 'x'\"], 1)",
                "p3-11 ([[10.0, 2.0]], 1)",
                "p3-12 ([[1000.0, -0.0, 2.0, 0.5]], 1)",
                "p3-13 ([['\"a\"']], 1)",
                "p3-14 ([['\"a']], 1)",
                "p3-15 ([\"Error: new-line character seen in unquoted field - do you need to open the file with newline=''?\"], 1)",
                "p3-16 ([['a', 'b'], ['c']], 2)",
                "p3-17 ([['a', 'b']], 1)",
                "p3-18 ([['a', 'b']], 1)",
                "p3-19 ([\"ValueError: could not convert string to float: 'a'\"], 1)",
                "p3-20 ([\"ValueError: could not convert string to float: 'a'\"], 1)",
                "w-obj ['\"x,y\"\\r\\n']",
                "w-obj-none ['\"\"\\r\\n']",
                "w-nonnum-obj ['\"3\"\\r\\n']",
                "w-notnull-obj ['\"\",\\r\\n']",
                "w-str-empty-nonnull ['\"\"\\r\\n']",
                "w-nonnum-single-empty ['\"\"\\r\\n']",
                "w-none-single-none-esc Error: single empty field record must be quoted",
                "w-floats2 ['0.0,-0.0,1.5,inf\\r\\n']",
                "w-bool ['True,False\\r\\n']",
                "w-nested ['[\\'a\\'],\"(\\'b\\',)\"\\r\\n']",
                "w-gen ['a,b\\r\\n']",
                "w-dict ['a,b\\r\\n']",
                "w-esc-both ['a\\\\,b\\r\\n']",
                "w-esc-quote-none ['a\\\\,b\\r\\n']",
                "w-esc-quote-q [\"a\\\\'b\\r\\n\"]",
                "w-esc-literal ['a\\\\\\\\b\\r\\n']",
                "w-q-notnull [',\"\",\"0\",\"False\"\\r\\n']",
                "w-q-strings [',\"\",0,False,\"s\"\\r\\n']",
                "w-q-nonnum ['\"\",\"\",0,False,\"s\"\\r\\n']",
                "w-q-all ['\"\",\"\",\"0\"\\r\\n']",
                "w-space-delim ['\"a b\" c\\r\\n']",
                "w-space-delim-q ['a\\\\ b c\\r\\n']",
                "w-qc ['|a||b|,c\\r\\n']",
                "w-qc-space [' a  b \\r\\n']",
                "w-term-cr ['a\\r']",
                "w-term-empty-field [',']",
                "r-dialect-inst [['a', 'b']]",
                "r-dialect-int ok [['a', 'b']]",
                "w-dialect-int ok ['a\\r\\n']",
                "w-dialect-none ok ['a\\r\\n']",
                "r-dialect-none ok [['a', 'b']]",
                "reg-missing-attrs None",
                "m3-w ok ['\"a;b\"\\r\\n']",
                "sniff-11lines ,",
                "sniff-12lines ;",
                "sniff-war ,",
                "sniff-quoted-single [('delimiter', '\\'\"\\''), ('quotechar', '\\'\"\\''), ('doublequote', 'False'), ('skipinitialspace', 'False')]",
                "sniff-quoted-doubled Error Could not determine delimiter",
                "sniff-quoted-tab 	",
                "sniff-apostrophe ,",
                "sniff-space-quote ,",
                "sniff-delims6 :",
                "sniff-delims7 :",
                "hh-21 Error Could not determine delimiter",
                "hh-irregular Error Could not determine delimiter",
                "guess-quote ('\"', False, ',', False)",
                "guess-delim (',', False)",
                "pref [',', '\\t', ';', ' ', ':']",
                "gq-none ('', False, None, 0)",
                "gd-none ('c', False)",
                "gq-missing TypeError Sniffer._guess_quote_and_delimiter() missing 2 required positional arguments: 'data' and 'delimiters'",
                "gd-missing TypeError Sniffer._guess_delimiter() missing 1 required positional argument: 'delimiters'",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The runtime's own gaps: `DictReader`, `DictWriter` and `Sniffer` are callables
    /// rather than classes, and the `StringIO`/`types` names the Python wrapper imports
    /// are absent. Everything else the module publishes is CPython's own list.
    /// </summary>
    [Fact]
    public void TheRuntimePinsItsOwnTypeObjectsAndGaps()
    {
        var output = Run(
            """"
            import csv
            print(type(csv.reader).__name__)
            r = csv.reader([])
            print(type(csv.DictReader).__name__, type(csv.DictWriter).__name__, type(csv.Sniffer).__name__)
            print(hasattr(csv, "StringIO"), hasattr(csv, "types"))
            print(type(r).__name__, type(r).__module__)
            print([n for n in dir(csv) if not n.startswith("_")])
            print(type(csv.Sniffer().sniff("a,b\n1,2\n")).__name__)
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "builtin_function_or_method",
                "builtin_function_or_method builtin_function_or_method builtin_function_or_method",
                "False False",
                "reader _csv",
                "['Dialect', 'DictReader', 'DictWriter', 'Error', 'QUOTE_ALL', 'QUOTE_MINIMAL', 'QUOTE_NONE', 'QUOTE_NONNUMERIC', 'QUOTE_NOTNULL', 'QUOTE_STRINGS', 'Sniffer', 'excel', 'excel_tab', 'field_size_limit', 'get_dialect', 'list_dialects', 'reader', 'register_dialect', 'unix_dialect', 'unregister_dialect', 'writer']",
                "type",
                ""
            ),
            output
        );
    }

    private static string Run(string source)
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            source,
            "<test>",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(
            result.Success,
            string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))
        );
        return output.ToString();
    }
}
