use rowan::ast::AstNode;
use crate::lossless::SyntaxNode;
use crate::Makefile;
use rowan::NodeOrToken;
use serde_json::{json, Value};

/// Convert the complete makefile syntax tree to JSON.
///
/// Every syntax node includes its kind, original text, and recursively
/// serialized children. Tokens include their kind and text.
pub fn to_value(makefile: &Makefile) -> Value {
    node_to_value(makefile.syntax())
}

fn node_to_value(node: &SyntaxNode) -> Value {
    let children: Vec<Value> = node
        .children_with_tokens()
        .map(|child| match child {
            NodeOrToken::Node(child_node) => node_to_value(&child_node),
            NodeOrToken::Token(token) => json!({
                "kind": format!("{:?}", token.kind()),
                "text": token.text(),
            }),
        })
        .collect();

    json!({
        "kind": format!("{:?}", node.kind()),
        "text": node.to_string(),
        "children": children,
    })
}

/// Convert a makefile syntax tree to a pretty-printed JSON string.
pub fn to_pretty_json(makefile: &Makefile) -> Result<String, serde_json::Error> {
    serde_json::to_string_pretty(&to_value(makefile))
}